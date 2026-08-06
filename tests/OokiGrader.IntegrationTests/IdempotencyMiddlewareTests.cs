using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
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
using OokiGrader.Host.Middleware;
using OokiGrader.Infrastructure.Persistence;

namespace OokiGrader.IntegrationTests;

public sealed class IdempotencyMiddlewareTests
{
    [Fact]
    public async Task SameKeyAndCanonicalJsonReplaysOriginalResponse()
    {
        await using var application = await IdempotencyTestApplication.CreateAsync();
        var key = Guid.NewGuid().ToString();

        var first = await application.PostAsync(
            key,
            """{"name":"採点","options":{"priority":2,"enabled":true}}""");
        var second = await application.PostAsync(
            key,
            """{"options":{"enabled":true,"priority":2},"name":"採点"}""");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(
            first.Headers.Location?.ToString(),
            second.Headers.Location?.ToString());
        Assert.Equal(
            first.Headers.ETag?.Tag,
            second.Headers.ETag?.Tag);
        Assert.Equal("true", second.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(
            await first.Content.ReadAsStringAsync(),
            await second.Content.ReadAsStringAsync());
        Assert.Equal(1, application.Counter.Value);
        Assert.Equal(1, await application.CountRecordsAsync());
    }

    [Fact]
    public async Task SameKeyWithDifferentRequestReturnsConflict()
    {
        await using var application = await IdempotencyTestApplication.CreateAsync();
        var key = Guid.NewGuid().ToString();

        var first = await application.PostAsync(key, """{"name":"first"}""");
        var conflict = await application.PostAsync(key, """{"name":"second"}""");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var problem = await conflict.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "IDEMPOTENCY_KEY_REUSED",
            problem.GetProperty("code").GetString());
        Assert.Equal(1, application.Counter.Value);
    }

    [Fact]
    public async Task MalformedKeyIsRejectedBeforeActionRuns()
    {
        await using var application = await IdempotencyTestApplication.CreateAsync();

        var response = await application.PostAsync("not-a-key", """{"name":"x"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, application.Counter.Value);
    }

    [Fact]
    public async Task MarkedEndpointWithoutKeyIsRejectedBeforeActionRuns()
    {
        await using var application = await IdempotencyTestApplication.CreateAsync();

        var response = await application.PostAsync(null, """{"name":"x"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "IDEMPOTENCY_KEY_REQUIRED",
            problem.GetProperty("code").GetString());
        Assert.Equal(0, application.Counter.Value);
        Assert.Equal(0, await application.CountRecordsAsync());
    }

    [Fact]
    public async Task UnmarkedEndpointWithoutKeyIsRejectedByDefault()
    {
        await using var application = await IdempotencyTestApplication.CreateAsync();

        var response = await application.PostAsync(
            null,
            """{"name":"x"}""",
            "/api/v1/optional-widgets");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, application.Counter.Value);
        Assert.Equal(0, await application.CountRecordsAsync());
    }

    [Fact]
    public async Task ExplicitProtocolOptOutContinuesWithoutPersistence()
    {
        await using var application = await IdempotencyTestApplication.CreateAsync();

        var response = await application.PostAsync(
            null,
            """{"name":"x"}""",
            "/api/v1/offset-protocol");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, application.Counter.Value);
        Assert.Equal(0, await application.CountRecordsAsync());
    }

    private sealed class IdempotencyTestApplication : IAsyncDisposable
    {
        private readonly IHost _host;
        private readonly SqliteConnection _connection;

        private IdempotencyTestApplication(
            IHost host,
            SqliteConnection connection,
            ActionCounter counter)
        {
            _host = host;
            _connection = connection;
            Counter = counter;
            Client = host.GetTestClient();
        }

        public HttpClient Client { get; }
        public ActionCounter Counter { get; }

        public static async Task<IdempotencyTestApplication> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var counter = new ActionCounter();
            var hostBuilder = new HostBuilder()
                .UseEnvironment(Environments.Development)
                .ConfigureWebHost(webBuilder =>
                {
                    webBuilder.UseTestServer();
                    webBuilder.ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddAuthorizationBuilder()
                            .SetFallbackPolicy(
                                new AuthorizationPolicyBuilder(
                                    TestAuthenticationHandler.SchemeName)
                                    .RequireAuthenticatedUser()
                                    .Build());
                        services
                            .AddAuthentication(TestAuthenticationHandler.SchemeName)
                            .AddScheme<
                                AuthenticationSchemeOptions,
                                TestAuthenticationHandler>(
                                TestAuthenticationHandler.SchemeName,
                                _ => { });
                        services.AddSingleton(TimeProvider.System);
                        services.AddSingleton(counter);
                        services.AddSingleton<IdempotencyLockProvider>();
                        services.AddSingleton(connection);
                        services.AddDbContext<OokiGraderDbContext>(
                            options => options.UseSqlite(connection));
                    });
                    webBuilder.Configure(application =>
                    {
                        application.UseRouting();
                        application.UseAuthentication();
                        application.UseAuthorization();
                        application.UseMiddleware<IdempotencyMiddleware>();
                        application.UseEndpoints(endpoints =>
                        {
                            endpoints.MapPost(
                                "/api/v1/widgets",
                                (HttpContext context, ActionCounter actionCounter) =>
                                {
                                    var value = actionCounter.Increment();
                                    context.Response.Headers.ETag = $"\"rev-{value}\"";
                                    return Results.Created(
                                        $"/api/v1/widgets/{value}",
                                        new { id = value });
                                })
                                .RequireIdempotency();
                            endpoints.MapPost(
                                "/api/v1/optional-widgets",
                                (ActionCounter actionCounter) =>
                                {
                                    var value = actionCounter.Increment();
                                    return Results.Created(
                                        $"/api/v1/optional-widgets/{value}",
                                        new { id = value });
                                });
                            endpoints.MapPost(
                                "/api/v1/offset-protocol",
                                (ActionCounter actionCounter) =>
                                {
                                    var value = actionCounter.Increment();
                                    return Results.Created(
                                        $"/api/v1/offset-protocol/{value}",
                                        new { id = value });
                                })
                                .AllowNonIdempotentMutation();
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
            return new IdempotencyTestApplication(host, connection, counter);
        }

        public async Task<HttpResponseMessage> PostAsync(
            string? key,
            string json,
            string path = "/api/v1/widgets")
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                path);
            if (key is not null)
            {
                request.Headers.Add("Idempotency-Key", key);
            }

            request.Content = new StringContent(
                json,
                System.Text.Encoding.UTF8,
                "application/json");
            return await Client.SendAsync(request);
        }

        public async Task<int> CountRecordsAsync()
        {
            await using var scope = _host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider
                .GetRequiredService<OokiGraderDbContext>();
            return await db.IdempotencyRecords.CountAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.StopAsync();
            _host.Dispose();
            await _connection.DisposeAsync();
        }
    }

    public sealed class ActionCounter
    {
        private int _value;

        public int Value => Volatile.Read(ref _value);

        public int Increment() => Interlocked.Increment(ref _value);
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
        public const string SchemeName = "IdempotencyIntegrationTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[]
            {
                new Claim(
                    ClaimTypes.NameIdentifier,
                    "01J00000000000000000000000"),
                new Claim(ClaimTypes.Name, "integration-user"),
            };
            var identity = new ClaimsIdentity(claims, SchemeName);
            return Task.FromResult(
                AuthenticateResult.Success(
                    new AuthenticationTicket(
                        new ClaimsPrincipal(identity),
                        SchemeName)));
        }
    }
}
