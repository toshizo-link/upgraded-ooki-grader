using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OokiGrader.Application.Abstractions;
using OokiGrader.Host.Api;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Preprocessing;

namespace OokiGrader.IntegrationTests;

public sealed class OrderedScanRoutingEndpointsTests
{
    [Fact]
    public async Task GenericScannerNameRoutesByTemplatePageContent()
    {
        await using var application = await RoutingApplication.CreateAsync(
            new SessionSeed("science", "理科", Marker: 1, "open"),
            new SessionSeed("math", "算数", Marker: 2, "open"),
            new SessionSeed("closed", "終了済み", Marker: 2, "closed"));

        using var response = await application.ClassifyAsync(
            marker: 2,
            "000001.pdf");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await ReadJsonAsync(response);
        var root = json.RootElement;
        Assert.Equal(
            "server_visual_alignment_v1",
            root.GetProperty("classificationMode").GetString());
        var openSessionIds = root.GetProperty("openSessions")
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!)
            .ToArray();
        Assert.Equal(["math", "science"], openSessionIds.Order().ToArray());
        var decision = root.GetProperty("items")[0];
        Assert.Equal("routed", decision.GetProperty("state").GetString());
        Assert.Equal(
            "math",
            decision.GetProperty("destinationSessionId").GetString());
        Assert.Equal(
            1,
            decision.GetProperty("detectedTemplatePageNumber").GetInt32());
        Assert.Contains(
            decision.GetProperty("candidates")[0]
                .GetProperty("evidence")
                .EnumerateArray(),
            item => item.GetString() == "visual_alignment");
    }

    [Fact]
    public async Task VisuallyAmbiguousPageNeedsTeacherReview()
    {
        await using var application = await RoutingApplication.CreateAsync(
            new SessionSeed("science", "理科", Marker: 1, "open"),
            new SessionSeed("math", "算数", Marker: 1, "open"));

        using var response = await application.ClassifyAsync(
            marker: 1,
            "000001.pdf");

        using var json = await ReadJsonAsync(response);
        var decision = json.RootElement.GetProperty("items")[0];
        Assert.Equal("needsReview", decision.GetProperty("state").GetString());
        Assert.Equal(
            "ROUTING_VISUAL_AMBIGUOUS",
            decision.GetProperty("issueCode").GetString());
        Assert.Equal(JsonValueKind.Null,
            decision.GetProperty("destinationSessionId").ValueKind);
    }

    [Fact]
    public async Task FilenameEvidenceRanksCandidatesButDoesNotOverrideVisualTie()
    {
        await using var application = await RoutingApplication.CreateAsync(
            new SessionSeed("science", "理科", Marker: 1, "open"),
            new SessionSeed("math", "算数", Marker: 1, "open"));

        using var response = await application.ClassifyAsync(
            marker: 1,
            "scan-算数-001.pdf");

        using var json = await ReadJsonAsync(response);
        var decision = json.RootElement.GetProperty("items")[0];
        Assert.Equal("needsReview", decision.GetProperty("state").GetString());
        Assert.Equal(
            "ROUTING_VISUAL_AMBIGUOUS",
            decision.GetProperty("issueCode").GetString());
        Assert.Equal(
            "math",
            decision.GetProperty("candidates")[0]
                .GetProperty("sessionId")
                .GetString());
        Assert.Contains(
            decision.GetProperty("candidates")
                .EnumerateArray()
                .SelectMany(candidate => candidate.GetProperty("evidence")
                    .EnumerateArray()),
            item => item.GetString() == "filename_title");
    }

    [Fact]
    public async Task WeakVisualMatchNeedsTeacherReview()
    {
        await using var application = await RoutingApplication.CreateAsync(
            new SessionSeed("science", "理科", Marker: 1, "open"),
            new SessionSeed("math", "算数", Marker: 2, "open"));

        using var response = await application.ClassifyAsync(
            marker: 7,
            "000001.pdf");

        using var json = await ReadJsonAsync(response);
        var decision = json.RootElement.GetProperty("items")[0];
        Assert.Equal("needsReview", decision.GetProperty("state").GetString());
        Assert.Equal(
            "ROUTING_VISUAL_WEAK",
            decision.GetProperty("issueCode").GetString());
    }

    [Fact]
    public async Task MultiplePagePdfIsRejectedBeforeRouting()
    {
        await using var application = await RoutingApplication.CreateAsync(
            new SessionSeed("science", "理科", Marker: 1, "open"));

        using var response = await application.ClassifyAsync(
            FakePreprocessingService.MultiplePageMarker,
            "000001.pdf");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var json = await ReadJsonAsync(response);
        Assert.Equal(
            "ORDERED_SCAN_ROUTING_PDF_INVALID",
            json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task SequentialRunBeyondSearchBurstIsNotThrottled()
    {
        await using var application = await RoutingApplication.CreateAsync(
            new SessionSeed("science", "理科", Marker: 1, "open"));

        for (var ordinal = 1; ordinal <= 65; ordinal++)
        {
            using var response = await application.ClassifyAsync(
                marker: 1,
                $"{ordinal:D4}.pdf",
                ordinal);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private sealed record SessionSeed(
        string Id,
        string Title,
        byte Marker,
        string State);

    private sealed class RoutingApplication : IAsyncDisposable
    {
        private readonly IHost _host;
        private readonly SqliteConnection _connection;

        private RoutingApplication(IHost host, SqliteConnection connection)
        {
            _host = host;
            _connection = connection;
            Client = host.GetTestClient();
        }

        private HttpClient Client { get; }

        public static async Task<RoutingApplication> CreateAsync(
            params SessionSeed[] sessions)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var contentStore = new MarkerContentStore();
            var host = new HostBuilder()
                .UseEnvironment(Environments.Development)
                .ConfigureWebHost(webBuilder =>
                {
                    webBuilder.UseTestServer();
                    webBuilder.ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddLogging();
                        services.AddSingleton(connection);
                        services.AddDbContext<OokiGraderDbContext>(
                            options => options.UseSqlite(connection));
                        services.AddSingleton<IContentStore>(contentStore);
                        services.AddSingleton<IPreprocessingService,
                            FakePreprocessingService>();
                        services.AddRateLimiter(options =>
                        {
                            options.RejectionStatusCode =
                                StatusCodes.Status429TooManyRequests;
                            options.AddPolicy(
                                OrderedScanRoutingEndpoints
                                    .ClassificationRateLimitPolicy,
                                _ => RateLimitPartition.GetConcurrencyLimiter(
                                    "ordered-scan-routing-site",
                                    _ => new ConcurrencyLimiterOptions
                                    {
                                        PermitLimit = 2,
                                        QueueLimit = 8,
                                        QueueProcessingOrder =
                                            QueueProcessingOrder.OldestFirst,
                                    }));
                        });
                        services
                            .AddAuthentication(TestAuthHandler.SchemeName)
                            .AddScheme<AuthenticationSchemeOptions,
                                TestAuthHandler>(
                                TestAuthHandler.SchemeName,
                                _ => { });
                        services.AddAuthorizationBuilder()
                            .SetFallbackPolicy(Policy())
                            .AddPolicy("upload", Policy());
                    });
                    webBuilder.Configure(application =>
                    {
                        application.UseRouting();
                        application.UseAuthentication();
                        application.UseRateLimiter();
                        application.UseAuthorization();
                        application.UseEndpoints(endpoints =>
                            endpoints.MapOrderedScanRoutingEndpoints());
                    });
                })
                .Build();
            try
            {
                await host.StartAsync();
                await using var scope = host.Services.CreateAsyncScope();
                var db = scope.ServiceProvider
                    .GetRequiredService<OokiGraderDbContext>();
                await db.Database.EnsureCreatedAsync();
                await SeedAsync(db, contentStore, sessions);
                return new RoutingApplication(host, connection);
            }
            catch
            {
                await host.StopAsync();
                host.Dispose();
                await connection.DisposeAsync();
                throw;
            }
        }

        public async Task<HttpResponseMessage> ClassifyAsync(
            byte marker,
            string fileName,
            int inputOrdinal = 1)
        {
            var bytes = new[] { marker };
            var path = "/api/v1/ordered-scan-routing:classify-page"
                + $"?clientItemId=scan-1&fileName={Uri.EscapeDataString(fileName)}"
                + $"&sizeBytes={bytes.Length}&inputOrdinal={inputOrdinal}";
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            request.Headers.Add(TestAuthHandler.RoleHeader, "scanOperator");
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(
                "application/pdf");
            return await Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.StopAsync();
            _host.Dispose();
            await _connection.DisposeAsync();
        }

        private static AuthorizationPolicy Policy() =>
            new AuthorizationPolicyBuilder(TestAuthHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireRole("scanOperator")
                .Build();

        private static async Task SeedAsync(
            OokiGraderDbContext db,
            MarkerContentStore contentStore,
            IReadOnlyList<SessionSeed> sessions)
        {
            var now = DateTimeOffset.UtcNow;
            db.StaffUsers.Add(new StaffUserEntity
            {
                Id = "teacher",
                Username = "routing.teacher",
                UsernameNormalized = "routing.teacher",
                DisplayName = "Routing teacher",
                PasswordHash = "argon2id:test",
                PasswordAlgorithm = "argon2id",
                PasswordAlgorithmVersion = 1,
                Status = "active",
                CredentialChangedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
            foreach (var session in sessions)
            {
                var templateId = $"template-{session.Id}";
                var versionId = $"version-{session.Id}";
                var uploadId = $"upload-{session.Id}";
                var fileObjectId = $"object-{session.Id}";
                var fileReferenceId = $"reference-{session.Id}";
                var sha256 = Convert.ToHexString(SHA256.HashData(
                        Encoding.UTF8.GetBytes(session.Id)))
                    .ToLowerInvariant();
                contentStore.Add(sha256, session.Marker);
                db.TestTemplates.Add(new TestTemplateEntity
                {
                    Id = templateId,
                    Title = session.Title,
                    State = "active",
                    CreatedByStaffUserId = "teacher",
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                db.TemplateVersions.Add(new TemplateVersionEntity
                {
                    Id = versionId,
                    TestTemplateId = templateId,
                    VersionNumber = 1,
                    State = "published",
                    PipelineVersion = "routing-test-v1",
                    ExpectedSubmissionPageCount = 1,
                    PublishedByStaffUserId = "teacher",
                    PublishedAt = now,
                    ContentHash = new string('a', 64),
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                db.UploadSessions.Add(new UploadSessionEntity
                {
                    Id = uploadId,
                    CreatedByStaffUserId = "teacher",
                    Purpose = "template_source",
                    DestinationType = "template_source",
                    OriginalFileName = $"{session.Id}.pdf",
                    DeclaredMimeType = "application/pdf",
                    ExpectedBytes = 1,
                    CurrentBytes = 1,
                    FinalSha256 = sha256,
                    IncomingRelativePath = $"routing/{session.Id}",
                    State = "completed",
                    ExpiresAt = now.AddHours(1),
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                db.FileObjects.Add(new FileObjectEntity
                {
                    Id = fileObjectId,
                    Sha256 = sha256,
                    Bytes = 1,
                    VerifiedMime = "application/pdf",
                    Extension = "pdf",
                    RelativeObjectPath = $"routing/{sha256}.pdf",
                    StorageClass = ContentStorageClass.TemplateSource.ToString(),
                    RetentionClass = "template_source",
                    State = "available",
                    CreatedAt = now,
                    VerifiedAt = now,
                    ReferenceCountCache = 1,
                });
                db.FileReferences.Add(new FileReferenceEntity
                {
                    Id = fileReferenceId,
                    FileObjectId = fileObjectId,
                    OwnerType = "upload_session",
                    OwnerId = uploadId,
                    Purpose = "template_source",
                    RetentionAnchorAt = now,
                    CreatedAt = now,
                });
                db.TemplateSources.Add(new TemplateSourceEntity
                {
                    Id = $"source-{session.Id}",
                    TemplateVersionId = versionId,
                    UploadSessionId = uploadId,
                    FileReferenceId = fileReferenceId,
                    SourceRole = "blank_test",
                    DisplayName = $"{session.Id}.pdf",
                    Ordinal = 0,
                    UploadedByStaffUserId = "teacher",
                    CreatedAt = now,
                });
                db.TestSessions.Add(new TestSessionEntity
                {
                    Id = session.Id,
                    TemplateVersionId = versionId,
                    TestDate = new DateOnly(2026, 8, 27),
                    Priority = "economy",
                    State = session.State,
                    CreatedByStaffUserId = "teacher",
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }

            await db.SaveChangesAsync();
        }
    }

    private sealed class MarkerContentStore : IContentStore
    {
        private readonly Dictionary<string, byte> _markers =
            new(StringComparer.Ordinal);

        public void Add(string sha256, byte marker) =>
            _markers.Add(sha256, marker);

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
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(new MemoryStream(
                [_markers[locator.Sha256]],
                writable: false));
        }

        public Task<bool> ExistsAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_markers.ContainsKey(locator.Sha256));

        public Task DeleteAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakePreprocessingService : IPreprocessingService
    {
        public const byte MultiplePageMarker = byte.MaxValue;

        public async Task<PreprocessingResult> ProcessAsync(
            Stream source,
            PreprocessingInput input,
            CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken);
            var marker = buffer.ToArray().Single();
            var pageCount = marker == MultiplePageMarker ? 2 : 1;
            var pages = Enumerable.Range(1, pageCount)
                .Select(pageNumber => Page(marker, pageNumber))
                .ToArray();
            return new PreprocessingResult(
                "routing-test-v1",
                marker.ToString("x2", CultureInfo.InvariantCulture),
                input.VerifiedMimeType,
                pages,
                [],
                "manifest");
        }

        public ImageArtifact Crop(
            PreprocessedPage page,
            MillionthsRegion region,
            int marginMillionths = 0) => page.NormalizedPng;

        public PageAlignmentResult AlignToReference(
            PreprocessedPage page,
            PreprocessedPage reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var score = page.Fingerprint.ExactSha256 ==
                reference.Fingerprint.ExactSha256
                    ? 9_000
                    : 5_000;
            return new PageAlignmentResult(
                page,
                "aligned",
                score,
                0,
                0,
                0,
                reference.Fingerprint.ExactSha256);
        }

        private static PreprocessedPage Page(byte marker, int pageNumber)
        {
            var markerText = marker.ToString(
                "x2",
                CultureInfo.InvariantCulture);
            var normalized = new ImageArtifact(
                "image/png",
                "png",
                1,
                1,
                [marker],
                markerText);
            var thumbnail = normalized with { Bytes = [marker] };
            return new PreprocessedPage(
                pageNumber,
                1,
                1,
                72,
                72,
                normalized,
                thumbnail,
                new PageQualityMetrics(
                    1,
                    0,
                    1,
                    0,
                    1,
                    0,
                    100,
                    false,
                    []),
                new PageFingerprint(markerText, markerText));
        }
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(
            options,
            logger,
            encoder)
    {
        public const string SchemeName = "routing-test";
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
                    new Claim(ClaimTypes.NameIdentifier, "teacher"),
                    new Claim(ClaimTypes.Name, "routing.teacher"),
                    new Claim(ClaimTypes.Role, role),
                ],
                SchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(
                    new ClaimsPrincipal(identity),
                    SchemeName)));
        }
    }
}
