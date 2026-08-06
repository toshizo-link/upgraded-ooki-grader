using System.Globalization;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OokiGrader.Host.Api;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.IntegrationTests;

public sealed class CursorPaginationIntegrationTests
{
    [Fact]
    public async Task StudentPagesMoveForwardWithoutDuplicates()
    {
        await using var application = await PaginationTestApplication.CreateAsync();

        var first = await application.GetAsync(
            "/api/v1/students?pageSize=2&status=active");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstPage = await ReadPageAsync(first);

        Assert.Equal(["student-01", "student-02"], firstPage.Ids);
        Assert.NotNull(firstPage.NextCursor);
        Assert.Equal(5, firstPage.TotalApproximate);

        var second = await application.GetAsync(
            "/api/v1/students?pageSize=2&status=active&cursor="
            + Uri.EscapeDataString(firstPage.NextCursor!));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondPage = await ReadPageAsync(second);

        Assert.Equal(["student-03", "student-04"], secondPage.Ids);
        Assert.NotNull(secondPage.NextCursor);
        Assert.Empty(firstPage.Ids.Intersect(secondPage.Ids));

        var third = await application.GetAsync(
            "/api/v1/students?pageSize=2&status=active&cursor="
            + Uri.EscapeDataString(secondPage.NextCursor!));
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        var thirdPage = await ReadPageAsync(third);

        Assert.Equal(["student-05"], thirdPage.Ids);
        Assert.Null(thirdPage.NextCursor);
        Assert.Equal(
            5,
            firstPage.Ids
                .Concat(secondPage.Ids)
                .Concat(thirdPage.Ids)
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Fact]
    public async Task CursorCannotCrossFiltersOrRoutes()
    {
        await using var application = await PaginationTestApplication.CreateAsync();
        var first = await application.GetAsync(
            "/api/v1/students?pageSize=1&status=active");
        var firstPage = await ReadPageAsync(first);
        var encoded = Uri.EscapeDataString(firstPage.NextCursor!);

        var differentFilter = await application.GetAsync(
            "/api/v1/students?pageSize=1&status=inactive&cursor=" + encoded);
        await AssertInvalidCursorAsync(differentFilter);

        var differentRoute = await application.GetAsync(
            "/api/v1/templates?pageSize=1&cursor=" + encoded);
        await AssertInvalidCursorAsync(differentRoute);
    }

    [Fact]
    public async Task TamperedCursorReturnsStableProblem()
    {
        await using var application = await PaginationTestApplication.CreateAsync();
        var first = await application.GetAsync(
            "/api/v1/students?pageSize=1&status=active");
        var firstPage = await ReadPageAsync(first);
        var cursor = firstPage.NextCursor!;
        var replacement = cursor[0] == 'A' ? 'B' : 'A';
        var tampered = replacement + cursor[1..];

        var response = await application.GetAsync(
            "/api/v1/students?pageSize=1&status=active&cursor="
            + Uri.EscapeDataString(tampered));

        await AssertInvalidCursorAsync(response);
    }

    private static async Task<Page> ReadPageAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        return new Page(
            root.GetProperty("items")
                .EnumerateArray()
                .Select(item => item.GetProperty("id").GetString()!)
                .ToArray(),
            root.GetProperty("nextCursor").ValueKind == JsonValueKind.Null
                ? null
                : root.GetProperty("nextCursor").GetString(),
            root.GetProperty("totalApproximate").GetInt32());
    }

    private static async Task AssertInvalidCursorAsync(
        HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "CURSOR_INVALID",
            problem.GetProperty("code").GetString());
    }

    private sealed record Page(
        IReadOnlyList<string> Ids,
        string? NextCursor,
        int TotalApproximate);

    private sealed class PaginationTestApplication : IAsyncDisposable
    {
        private readonly IHost _host;
        private readonly SqliteConnection _connection;

        private PaginationTestApplication(
            IHost host,
            SqliteConnection connection)
        {
            _host = host;
            _connection = connection;
            Client = host.GetTestClient();
            Client.Timeout = TimeSpan.FromSeconds(5);
        }

        private HttpClient Client { get; }

        public static async Task<PaginationTestApplication> CreateAsync()
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
                            .SetFallbackPolicy(
                                new AuthorizationPolicyBuilder(
                                    TestAuthenticationHandler.SchemeName)
                                    .RequireAuthenticatedUser()
                                    .Build())
                            .AddPolicy(
                                "results",
                                policy => policy
                                    .AddAuthenticationSchemes(
                                        TestAuthenticationHandler.SchemeName)
                                    .RequireRole("teacher"))
                            .AddPolicy(
                                "teacher",
                                policy => policy
                                    .AddAuthenticationSchemes(
                                        TestAuthenticationHandler.SchemeName)
                                    .RequireRole("teacher"));
                    });
                    webBuilder.Configure(application =>
                    {
                        application.UseRouting();
                        application.UseAuthentication();
                        application.UseAuthorization();
                        application.UseEndpoints(endpoints =>
                        {
                            endpoints.MapStudentsEndpoints();
                            endpoints.MapTemplatesEndpoints();
                        });
                    });
                });

            var host = hostBuilder.Build();
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<OokiGraderDbContext>();
                await db.Database.EnsureCreatedAsync();
                var now = new DateTimeOffset(
                    2026,
                    7,
                    27,
                    0,
                    0,
                    0,
                    TimeSpan.Zero);
                for (var index = 1; index <= 5; index++)
                {
                    var number = index.ToString(
                        "D3",
                        CultureInfo.InvariantCulture);
                    db.Students.Add(new StudentEntity
                    {
                        Id = $"student-{index:D2}",
                        StudentNumber = number,
                        StudentNumberNormalized = number,
                        FamilyName = $"姓{index}",
                        GivenName = $"名{index}",
                        FamilyNameNormalized = $"姓{index}",
                        GivenNameNormalized = $"名{index}",
                        DisplayName = $"姓{index} 名{index}",
                        Status = "active",
                        CreatedAt = now.AddMinutes(index),
                        UpdatedAt = now.AddMinutes(index),
                    });
                }

                await db.SaveChangesAsync();
            }

            await host.StartAsync();
            return new PaginationTestApplication(host, connection);
        }

        public async Task<HttpResponseMessage> GetAsync(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add(
                TestAuthenticationHandler.RoleHeader,
                "teacher");
            return await Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.StopAsync();
            _host.Dispose();
            await _connection.DisposeAsync();
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
        public const string SchemeName = "CursorPaginationIntegrationTest";
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
                    new Claim(ClaimTypes.NameIdentifier, "pagination-test-user"),
                    new Claim(ClaimTypes.Name, "pagination-test-user"),
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
