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
using OokiGrader.Domain.Templates;
using OokiGrader.Host.Api;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.IntegrationTests;

public sealed class RobustListQueryIntegrationTests
{
    [Fact]
    public async Task StudentsCombineNormalizedSearchFiltersAndFullFacets()
    {
        await using var application = await ListTestApplication.CreateAsync();

        var response = await application.GetAsync(
            "/api/v1/students?status=active&class=A&course=標準&grade=小4"
            + "&search=　００１　&sort=name&pageSize=1&includeFacets=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;
        Assert.Equal(
            "student-01",
            root.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal(1, root.GetProperty("totalApproximate").GetInt32());
        Assert.Contains(
            root.GetProperty("facets").GetProperty("classes")
                .EnumerateArray(),
            item => item.GetProperty("value").GetString() == "ページ外");
    }

    [Fact]
    public async Task StudentsSearchConcatenatedFullNameKanaWithoutWhitespace()
    {
        await using var application = await ListTestApplication.CreateAsync();

        var response = await application.GetAsync(
            "/api/v1/students?search=サトウハナコ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await ReadJsonAsync(response);
        Assert.Equal(["student-02"], Ids(document.RootElement));
    }

    [Fact]
    public async Task StudentSortCursorUsesIdTieBreakerAndBindsSort()
    {
        await using var application = await ListTestApplication.CreateAsync();
        var first = await application.GetAsync(
            "/api/v1/students?status=active&sort=-updatedAt&pageSize=2");
        Assert.True(
            first.IsSuccessStatusCode,
            await first.Content.ReadAsStringAsync());
        using var firstJson = await ReadJsonAsync(first);
        var firstIds = Ids(firstJson.RootElement);
        var cursor = firstJson.RootElement.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);

        var second = await application.GetAsync(
            "/api/v1/students?status=active&sort=-updatedAt&pageSize=2&cursor="
            + Uri.EscapeDataString(cursor!));
        using var secondJson = await ReadJsonAsync(second);
        var secondIds = Ids(secondJson.RootElement);

        Assert.Empty(firstIds.Intersect(secondIds, StringComparer.Ordinal));
        Assert.Equal(4, firstIds.Concat(secondIds).Distinct().Count());
        var changedSort = await application.GetAsync(
            "/api/v1/students?status=active&sort=name&pageSize=2&cursor="
            + Uri.EscapeDataString(cursor!));
        await AssertProblemAsync(changedSort, "CURSOR_INVALID");
    }

    [Fact]
    public async Task SessionsCombineDateTemplateClassCourseSortAndFacets()
    {
        await using var application = await ListTestApplication.CreateAsync();

        var response = await application.GetAsync(
            "/api/v1/test-sessions?from=2026-07-02&to=2026-07-02"
            + "&templateId=template-02&class=B&course=進学&sort=name"
            + "&pageSize=1&includeFacets=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;
        Assert.Equal(
            "session-02",
            root.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Contains(
            root.GetProperty("facets").GetProperty("templates")
                .EnumerateArray(),
            item => item.GetProperty("value").GetString() == "template-01");
    }

    [Fact]
    public async Task TemplatesCombineLifecycleMetadataTypeUnicodeSearchAndFacets()
    {
        await using var application = await ListTestApplication.CreateAsync();

        var response = await application.GetAsync(
            "/api/v1/templates?state=active&subject=国語&category=HOP"
            + "&course=標準&grade=小4&testType=hop&search=ＡＢＣ"
            + "&sort=name&pageSize=1&includeFacets=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;
        Assert.Equal(
            "template-01",
            root.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Contains(
            root.GetProperty("facets").GetProperty("testTypes")
                .EnumerateArray(),
            item => item.GetProperty("value").GetString()
                == "classPlacement");

        var ordinary = await application.GetAsync("/api/v1/templates");
        using var ordinaryJson = await ReadJsonAsync(ordinary);
        Assert.DoesNotContain(
            Ids(ordinaryJson.RootElement),
            id => id == "template-03");
    }

    [Fact]
    public async Task FinalizedReportsCombineAllFiltersAndExposeFullFacets()
    {
        await using var application = await ListTestApplication.CreateAsync();

        var response = await application.GetAsync(
            "/api/v1/submissions?state=finalized&from=2026-07-01&to=2026-07-01"
            + "&studentId=student-01&templateId=template-01&subject=国語"
            + "&category=HOP&course=標準&class=A&search=ＡＢＣ%20山田"
            + "&sort=testTitle&pageSize=1&includeFacets=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;
        Assert.Equal(
            "submission-01",
            root.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal(
            "国語 月例",
            root.GetProperty("items")[0].GetProperty("testTitle").GetString());
        Assert.Contains(
            root.GetProperty("facets").GetProperty("templates")
                .EnumerateArray(),
            item => item.GetProperty("value").GetString() == "template-02");
    }

    [Fact]
    public async Task FinalizedReportSortPaginationIsStable()
    {
        await using var application = await ListTestApplication.CreateAsync();
        var first = await application.GetAsync(
            "/api/v1/submissions?state=finalized&sort=-testDate&pageSize=2");
        Assert.True(
            first.IsSuccessStatusCode,
            await first.Content.ReadAsStringAsync());
        using var firstJson = await ReadJsonAsync(first);
        var cursor = firstJson.RootElement.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);

        var second = await application.GetAsync(
            "/api/v1/submissions?state=finalized&sort=-testDate&pageSize=2&cursor="
            + Uri.EscapeDataString(cursor!));
        using var secondJson = await ReadJsonAsync(second);

        var all = Ids(firstJson.RootElement).Concat(Ids(secondJson.RootElement))
            .ToArray();
        Assert.Equal(3, all.Length);
        Assert.Equal(3, all.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("readyForReview", "submission-04")]
    [InlineData("awaitingAi", "submission-05")]
    public async Task SubmissionVirtualStatesResolveToPersistedStates(
        string state,
        string expectedId)
    {
        await using var application = await ListTestApplication.CreateAsync();

        var response = await application.GetAsync(
            "/api/v1/submissions?state=" + state);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await ReadJsonAsync(response);
        Assert.Contains(expectedId, Ids(document.RootElement));
    }

    [Theory]
    [InlineData("/api/v1/test-sessions?sort=testDate")]
    [InlineData("/api/v1/templates?sort=subject")]
    [InlineData("/api/v1/submissions?state=finalized&sort=testTitle")]
    public async Task AlternateSortCursorsRemainStable(string basePath)
    {
        await using var application = await ListTestApplication.CreateAsync();
        var separator = basePath.Contains('?')
            ? "&"
            : "?";
        var first = await application.GetAsync(basePath + separator + "pageSize=1");
        Assert.True(
            first.IsSuccessStatusCode,
            await first.Content.ReadAsStringAsync());
        using var firstJson = await ReadJsonAsync(first);
        var cursor = firstJson.RootElement.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);

        var second = await application.GetAsync(
            basePath
            + separator
            + "pageSize=1&cursor="
            + Uri.EscapeDataString(cursor!));
        Assert.True(
            second.IsSuccessStatusCode,
            await second.Content.ReadAsStringAsync());
        using var secondJson = await ReadJsonAsync(second);

        Assert.NotEqual(
            Ids(firstJson.RootElement).Single(),
            Ids(secondJson.RootElement).Single());
    }

    [Theory]
    [InlineData("/api/v1/students?status=unknown")]
    [InlineData("/api/v1/students?status=active&active=false")]
    [InlineData("/api/v1/students?sort=createdAt")]
    [InlineData("/api/v1/students?limit=201")]
    [InlineData("/api/v1/test-sessions?state=unknown")]
    [InlineData("/api/v1/test-sessions?pageSize=0")]
    [InlineData("/api/v1/test-sessions?from=2026-07-03&to=2026-07-02")]
    [InlineData("/api/v1/templates?testType=quiz")]
    [InlineData("/api/v1/templates?pageSize=201")]
    [InlineData("/api/v1/templates?sort=grade")]
    [InlineData("/api/v1/submissions?state=definitely_unknown")]
    [InlineData("/api/v1/submissions?limit=0")]
    [InlineData("/api/v1/submissions?state=finalized&sort=score")]
    [InlineData("/api/v1/submissions?state=finalized&from=2026-07-03&to=2026-07-02")]
    public async Task InvalidFiltersAndSortsReturnStableProblem(string path)
    {
        await using var application = await ListTestApplication.CreateAsync();

        var response = await application.GetAsync(path);

        await AssertProblemAsync(response, "LIST_QUERY_INVALID");
    }

    [Fact]
    public async Task SearchLengthAndPageSizeAreBounded()
    {
        await using var application = await ListTestApplication.CreateAsync();
        var longSearch = new string('あ', 201);

        var invalid = await application.GetAsync(
            "/api/v1/students?search=" + Uri.EscapeDataString(longSearch));
        await AssertProblemAsync(invalid, "LIST_QUERY_INVALID");

        var invalidPage = await application.GetAsync(
            "/api/v1/students?pageSize=999999&status=active");
        await AssertProblemAsync(invalidPage, "LIST_QUERY_INVALID");
    }

    private static string[] Ids(JsonElement root) =>
        root.GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!)
            .ToArray();

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        string code)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = await ReadJsonAsync(response);
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
    }

    private sealed class ListTestApplication : IAsyncDisposable
    {
        private readonly IHost _host;
        private readonly SqliteConnection _connection;

        private ListTestApplication(IHost host, SqliteConnection connection)
        {
            _host = host;
            _connection = connection;
            Client = host.GetTestClient();
            Client.Timeout = TimeSpan.FromSeconds(10);
        }

        private HttpClient Client { get; }

        public static async Task<ListTestApplication> CreateAsync()
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
                        services.AddDataProtection();
                        services.AddSingleton<ProtectedCursorCodec>();
                        services.AddSingleton(connection);
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
                            .SetFallbackPolicy(Policy("teacher"))
                            .AddPolicy("results", Policy("teacher"))
                            .AddPolicy("teacher", Policy("teacher"))
                            .AddPolicy("upload", Policy("teacher"))
                            .AddPolicy("review", Policy("teacher"));
                    });
                    webBuilder.Configure(application =>
                    {
                        application.UseRouting();
                        application.UseAuthentication();
                        application.UseAuthorization();
                        application.UseEndpoints(endpoints =>
                        {
                            endpoints.MapStudentsEndpoints();
                            endpoints.MapTestSessionsEndpoints();
                            endpoints.MapTemplatesEndpoints();
                            endpoints.MapSubmissionsEndpoints();
                        });
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
            return new ListTestApplication(host, connection);
        }

        public async Task<HttpResponseMessage> GetAsync(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add(TestAuthenticationHandler.RoleHeader, "teacher");
            return await Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.StopAsync();
            _host.Dispose();
            await _connection.DisposeAsync();
        }

        private static AuthorizationPolicy Policy(string role) =>
            new AuthorizationPolicyBuilder(TestAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireRole(role)
                .Build();

        private static async Task SeedAsync(OokiGraderDbContext db)
        {
            var now = new DateTimeOffset(
                2026,
                7,
                30,
                0,
                0,
                0,
                TimeSpan.Zero);
            db.SiteSettings.Add(new SiteSettingsEntity
            {
                Id = "site",
                SchoolName = "一覧試験塾",
                DataRoot = "/tmp/list-tests",
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.StaffUsers.Add(new StaffUserEntity
            {
                Id = "staff-01",
                Username = "teacher",
                UsernameNormalized = "teacher",
                DisplayName = "先生",
                PasswordHash = "test",
                PasswordAlgorithm = "test",
                PasswordAlgorithmVersion = 1,
                CredentialChangedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });

            var students = new[]
            {
                Student("student-01", "001", "山田", "太郎", "A", "標準", "小4", true, now),
                Student("student-02", "002", "佐藤", "花子", "B", "進学", "小6", true, now),
                Student("student-03", "１２３", "高橋", "次郎", "ページ外", "標準", "小4", true, now),
                Student("student-04", "004", "鈴木", "三郎", "C", "基礎", "小5", false, now),
                Student("student-05", "005", "伊藤", "四郎", "D", "基礎", "小5", true, now),
            };
            students[1].FamilyNameKana = "サトウ";
            students[1].GivenNameKana = "ハナコ";
            students[1].FamilyNameKanaNormalized = "サトウ";
            students[1].GivenNameKanaNormalized = "ハナコ";
            db.Students.AddRange(students);

            var templates = new[]
            {
                Template("template-01", "version-01", "ABC 国語 HOP", "国語", "HOP", "標準", "小4", "active", TestType.Hop, now),
                Template("template-02", "version-02", "理科 STEP", "理科", "演習", "進学", "小6", "active", TestType.Step, now),
                Template("template-03", "version-03", "社会 クラス分け", "社会", "クラス分け", "標準", "小5", "archived", TestType.ClassPlacement, now),
            };
            db.TestTemplates.AddRange(templates.Select(item => item.Template));
            db.TemplateVersions.AddRange(templates.Select(item => item.Version));

            var sessions = new[]
            {
                Session("session-01", "version-01", "国語 月例", new DateOnly(2026, 7, 1), "A", "標準", now),
                Session("session-02", "version-02", null, new DateOnly(2026, 7, 2), "B", "進学", now),
                Session("session-03", "version-01", "国語 追試", new DateOnly(2026, 7, 2), "ページ外", "標準", now),
            };
            db.TestSessions.AddRange(sessions);
            db.Submissions.AddRange(
                Submission("submission-01", "session-01", "student-01", "finalized", now.AddHours(1), now),
                Submission("submission-02", "session-02", "student-02", "finalized", now.AddHours(2), now),
                Submission("submission-03", "session-03", "student-03", "finalized", now.AddHours(3), now),
                Submission("submission-04", "session-01", "student-05", "needs_grade_review", null, now),
                Submission("submission-05", "session-02", "student-04", "grading", null, now));
            await db.SaveChangesAsync();
        }

        private static StudentEntity Student(
            string id,
            string number,
            string family,
            string given,
            string schoolClass,
            string course,
            string grade,
            bool active,
            DateTimeOffset now)
        {
            var normalizedNumber = number.Normalize(
                System.Text.NormalizationForm.FormKC);
            return new StudentEntity
            {
                Id = id,
                StudentNumber = number,
                StudentNumberNormalized = normalizedNumber,
                FamilyName = family,
                GivenName = given,
                FamilyNameNormalized = family,
                GivenNameNormalized = given,
                DisplayName = family + " " + given,
                SchoolClass = schoolClass,
                Course = course,
                GradeLabel = grade,
                Status = active ? "active" : "inactive",
                CreatedAt = now,
                UpdatedAt = now,
            };
        }

        private static (TestTemplateEntity Template, TemplateVersionEntity Version)
            Template(
                string id,
                string versionId,
                string title,
                string subject,
                string category,
                string course,
                string grade,
                string state,
                TestType testType,
                DateTimeOffset now)
        {
            var template = new TestTemplateEntity
            {
                Id = id,
                Title = title,
                Subject = subject,
                Category = category,
                Course = course,
                GradeLabel = grade,
                State = state,
                CreatedByStaffUserId = "staff-01",
                CreatedAt = now,
                UpdatedAt = now,
            };
            var version = new TemplateVersionEntity
            {
                Id = versionId,
                TestTemplateId = id,
                TestTemplate = template,
                VersionNumber = 1,
                State = "draft",
                PipelineVersion = "test",
                TestType = testType,
                CreatedAt = now,
                UpdatedAt = now,
            };
            template.Versions.Add(version);
            return (template, version);
        }

        private static TestSessionEntity Session(
            string id,
            string versionId,
            string? title,
            DateOnly date,
            string schoolClass,
            string course,
            DateTimeOffset now) => new()
            {
                Id = id,
                TemplateVersionId = versionId,
                TitleOverride = title,
                TestDate = date,
                ClassLabel = schoolClass,
                Course = course,
                State = "closed",
                Priority = "economy",
                CreatedByStaffUserId = "staff-01",
                CreatedAt = now,
                UpdatedAt = now,
            };

        private static SubmissionEntity Submission(
            string id,
            string sessionId,
            string studentId,
            string state,
            DateTimeOffset? finalizedAt,
            DateTimeOffset now) => new()
            {
                Id = id,
                TestSessionId = sessionId,
                AssignedStudentId = studentId,
                State = state,
                AssignmentMethod = "teacher",
                CanonicalForSession = true,
                UploadedByStaffUserId = "staff-01",
                UploadCompletedAt = now,
                FinalizedByStaffUserId = finalizedAt.HasValue ? "staff-01" : null,
                FinalizedAt = finalizedAt,
                CreatedAt = now,
                UpdatedAt = now,
            };
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
        public const string SchemeName = "RobustListQueryIntegrationTest";
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
                    new Claim(ClaimTypes.NameIdentifier, "staff-01"),
                    new Claim(ClaimTypes.Name, "teacher"),
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
}
