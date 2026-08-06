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
using OokiGrader.Application.Identifiers;
using OokiGrader.Host.Api;
using OokiGrader.Host.Security;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.IntegrationTests;

public sealed class StaffWorkflowTests
{
    private const string StrongInitialPassword = "Initial temporary passphrase 2026";
    private const string StrongResetPassword = "Replacement temporary passphrase 2026";
    private static readonly string[] TeacherRole = ["teacher"];
    private static readonly string[] TeacherAndScanRoles =
        ["teacher", "scanOperator"];

    [Fact]
    public async Task AdministratorCanManageLifecycleWithoutExposingCredentials()
    {
        await using var application = await StaffTestApplication.CreateAsync();

        var createdResponse = await application.SendAsync(
            HttpMethod.Post,
            "/api/v1/staff",
            TestAuthenticationHandler.AdministratorId,
            "administrator",
            new
            {
                username = "teacher.one",
                displayName = "採点担当 一",
                initialPassword = StrongInitialPassword,
                roles = TeacherRole,
            });
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        Assert.NotNull(createdResponse.Headers.ETag);
        var created = await ReadJsonAsync(createdResponse);
        var staffId = RequiredString(created, "id");
        Assert.True(created.GetProperty("mustChangePassword").GetBoolean());
        Assert.DoesNotContain(
            StrongInitialPassword,
            created.GetRawText(),
            StringComparison.Ordinal);

        var listedResponse = await application.SendAsync(
            HttpMethod.Get,
            "/api/v1/staff?search=採点担当",
            TestAuthenticationHandler.AdministratorId,
            "administrator");
        var listed = await ReadJsonAsync(listedResponse);
        Assert.Single(listed.GetProperty("items").EnumerateArray());

        var patchedResponse = await application.SendAsync(
            HttpMethod.Patch,
            $"/api/v1/staff/{staffId}",
            TestAuthenticationHandler.AdministratorId,
            "administrator",
            new
            {
                displayName = "採点・読取担当",
                roles = TeacherAndScanRoles,
            },
            createdResponse.Headers.ETag?.Tag);
        Assert.Equal(HttpStatusCode.OK, patchedResponse.StatusCode);
        var patched = await ReadJsonAsync(patchedResponse);
        Assert.Equal(
            ["scanOperator", "teacher"],
            patched.GetProperty("roles")
                .EnumerateArray()
                .Select(value => value.GetString()!)
                .ToArray());

        await application.WithDatabaseAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.StaffSessions.Add(new StaffSessionEntity
            {
                IdHash = "existing-session",
                StaffUserId = staffId,
                CreatedAt = now,
                LastSeenAt = now,
                IdleExpiresAt = now.AddMinutes(30),
                AbsoluteExpiresAt = now.AddHours(12),
                CsrfSecretHash = "csrf",
            });
            await db.SaveChangesAsync();
        });

        var resetResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/staff/{staffId}:resetPassword",
            TestAuthenticationHandler.AdministratorId,
            "administrator",
            new
            {
                newPassword = StrongResetPassword,
                reasonCode = "administrator_reset",
            },
            patchedResponse.Headers.ETag?.Tag);
        Assert.Equal(HttpStatusCode.OK, resetResponse.StatusCode);
        var reset = await ReadJsonAsync(resetResponse);
        Assert.True(reset.GetProperty("mustChangePassword").GetBoolean());
        Assert.True(
            reset.GetProperty("passwordSetupExpiresAt").GetDateTimeOffset()
            > DateTimeOffset.UtcNow);

        var disabledResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/staff/{staffId}:disable",
            TestAuthenticationHandler.AdministratorId,
            "administrator",
            new { reasonCode = "employment_ended" },
            resetResponse.Headers.ETag?.Tag);
        Assert.Equal(HttpStatusCode.OK, disabledResponse.StatusCode);
        var disabled = await ReadJsonAsync(disabledResponse);
        Assert.Equal("disabled", RequiredString(disabled, "status"));

        var enabledResponse = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/staff/{staffId}:enable",
            TestAuthenticationHandler.AdministratorId,
            "administrator",
            new { reasonCode = "employment_resumed" },
            disabledResponse.Headers.ETag?.Tag);
        Assert.Equal(HttpStatusCode.OK, enabledResponse.StatusCode);

        await application.WithDatabaseAsync(async db =>
        {
            var session = await db.StaffSessions
                .AsNoTracking()
                .SingleAsync(item => item.IdHash == "existing-session");
            Assert.NotNull(session.RevokedAt);
            Assert.Equal("administrator_password_reset", session.RevokeReason);

            var eventTypes = await db.AuditEvents
                .AsNoTracking()
                .Where(item => item.ObjectId == staffId)
                .Select(item => item.EventType)
                .ToListAsync();
            Assert.Contains("staff.created", eventTypes);
            Assert.Contains("staff.updated", eventTypes);
            Assert.Contains("staff.password_reset", eventTypes);
            Assert.Contains("staff.disabled", eventTypes);
            Assert.Contains("staff.enabled", eventTypes);
        });
    }

    [Fact]
    public async Task LastEnabledAdministratorCannotBeDisabledOrLoseRole()
    {
        await using var application = await StaffTestApplication.CreateAsync();
        var administrator = await application.GetStaffAsync(
            TestAuthenticationHandler.AdministratorId);
        var etag = administrator.Headers.ETag?.Tag;

        var disable = await application.SendAsync(
            HttpMethod.Post,
            $"/api/v1/staff/{TestAuthenticationHandler.AdministratorId}:disable",
            TestAuthenticationHandler.AdministratorId,
            "administrator",
            new { reasonCode = "test_disable" },
            etag);
        Assert.Equal(HttpStatusCode.Conflict, disable.StatusCode);
        Assert.Equal(
            "LAST_ADMINISTRATOR_REQUIRED",
            RequiredString(await ReadJsonAsync(disable), "code"));

        var removeRole = await application.SendAsync(
            HttpMethod.Patch,
            $"/api/v1/staff/{TestAuthenticationHandler.AdministratorId}",
            TestAuthenticationHandler.AdministratorId,
            "administrator",
            new { roles = TeacherRole },
            etag);
        Assert.Equal(HttpStatusCode.Conflict, removeRole.StatusCode);
    }

    [Fact]
    public async Task PasswordChangeClearsSetupRequirementAndRevokesOtherSessions()
    {
        await using var application = await StaffTestApplication.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var userId = UlidId.New(now);
        await application.WithDatabaseAsync(async db =>
        {
            db.StaffUsers.Add(new StaffUserEntity
            {
                Id = userId,
                Username = "setup.user",
                UsernameNormalized = "SETUP.USER",
                DisplayName = "初回設定",
                PasswordHash = FakePasswordHasher.Encode(StrongInitialPassword),
                PasswordAlgorithm = "test",
                PasswordAlgorithmVersion = 1,
                Status = "active",
                CredentialChangedAt = now,
                MustChangePassword = true,
                PasswordSetupExpiresAt = now.AddMinutes(30),
                PasswordSetupUsedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.StaffUserRoles.Add(new StaffUserRoleEntity
            {
                StaffUserId = userId,
                RoleName = "teacher",
                GrantedByStaffUserId = TestAuthenticationHandler.AdministratorId,
                GrantedAt = now,
            });
            db.StaffSessions.AddRange(
                Session(userId, "current-session", now),
                Session(userId, "other-session", now));
            await db.SaveChangesAsync();
        });

        var response = await application.SendAsync(
            HttpMethod.Post,
            "/api/v1/auth/change-password",
            userId,
            "teacher",
            new
            {
                currentPassword = StrongInitialPassword,
                newPassword = "A new staff passphrase for 2026",
            },
            sessionHash: "current-session");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await application.WithDatabaseAsync(async db =>
        {
            var user = await db.StaffUsers
                .AsNoTracking()
                .SingleAsync(item => item.Id == userId);
            Assert.False(user.MustChangePassword);
            Assert.Null(user.PasswordSetupExpiresAt);
            Assert.Null(user.PasswordSetupUsedAt);
            Assert.True(
                await new FakePasswordHasher().VerifyAsync(
                    "A new staff passphrase for 2026",
                    user.PasswordHash));

            var sessions = await db.StaffSessions
                .AsNoTracking()
                .Where(item => item.StaffUserId == userId)
                .ToDictionaryAsync(item => item.IdHash);
            Assert.Null(sessions["current-session"].RevokedAt);
            Assert.NotNull(sessions["other-session"].RevokedAt);
        });
    }

    [Fact]
    public async Task TemporaryPasswordIsSingleUseAndLoginAuditsMaskedIpPrefix()
    {
        await using var application = await StaffTestApplication.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var userId = UlidId.New(now);
        await application.WithDatabaseAsync(async db =>
        {
            db.StaffUsers.Add(new StaffUserEntity
            {
                Id = userId,
                Username = "single.use",
                UsernameNormalized = "SINGLE.USE",
                DisplayName = "一回利用",
                PasswordHash = FakePasswordHasher.Encode(StrongResetPassword),
                PasswordAlgorithm = "test",
                PasswordAlgorithmVersion = 1,
                Status = "active",
                CredentialChangedAt = now,
                MustChangePassword = true,
                PasswordSetupExpiresAt = now.AddMinutes(30),
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.StaffUserRoles.Add(new StaffUserRoleEntity
            {
                StaffUserId = userId,
                RoleName = "teacher",
                GrantedByStaffUserId = TestAuthenticationHandler.AdministratorId,
                GrantedAt = now,
            });
            await db.SaveChangesAsync();
        });

        var first = await application.LoginWithServiceAsync(
            "single.use",
            StrongResetPassword,
            IPAddress.Parse("192.168.7.44"));
        var second = await application.LoginWithServiceAsync(
            "single.use",
            StrongResetPassword,
            IPAddress.Parse("192.168.7.99"));

        Assert.Equal(LoginDisposition.Succeeded, first.Disposition);
        Assert.True(first.Session?.Staff.MustChangePassword);
        Assert.Equal(LoginDisposition.InvalidCredentials, second.Disposition);
        await application.WithDatabaseAsync(async db =>
        {
            var audits = await db.AuditEvents
                .AsNoTracking()
                .Where(item => item.ObjectId == userId)
                .OrderBy(item => item.OccurredAt)
                .ToListAsync();
            Assert.Equal(2, audits.Count);
            Assert.All(
                audits,
                item => Assert.Equal("192.168.7.0/24", item.SourceIpPrefix));
            Assert.Equal("password_setup_already_used", audits[1].ReasonCode);
        });
    }

    private static StaffSessionEntity Session(
        string staffId,
        string idHash,
        DateTimeOffset now) =>
        new()
        {
            IdHash = idHash,
            StaffUserId = staffId,
            CreatedAt = now,
            LastSeenAt = now,
            IdleExpiresAt = now.AddMinutes(30),
            AbsoluteExpiresAt = now.AddHours(12),
            CsrfSecretHash = $"csrf-{idHash}",
        };

    private static async Task<JsonElement> ReadJsonAsync(
        HttpResponseMessage response)
    {
        var value = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(JsonValueKind.Undefined, value.ValueKind);
        return value;
    }

    private static string RequiredString(JsonElement value, string propertyName)
    {
        var result = value.GetProperty(propertyName).GetString();
        Assert.False(string.IsNullOrWhiteSpace(result));
        return result!;
    }

    private sealed class StaffTestApplication : IAsyncDisposable
    {
        private readonly IHost _host;
        private readonly SqliteConnection _connection;

        private StaffTestApplication(IHost host, SqliteConnection connection)
        {
            _host = host;
            _connection = connection;
            Client = host.GetTestClient();
            Client.Timeout = TimeSpan.FromSeconds(5);
        }

        public HttpClient Client { get; }

        public static async Task<StaffTestApplication> CreateAsync()
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
                        services.AddSingleton(TimeProvider.System);
                        services.AddSingleton<IPasswordHasher, FakePasswordHasher>();
                        services.AddSingleton<ISessionTokenService, SessionTokenService>();
                        services.AddScoped<
                            IStaffAuthenticationService,
                            StaffAuthenticationService>();
                        services.AddScoped<IBootstrapService, BootstrapService>();
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
                        application.UseEndpoints(endpoints =>
                        {
                            endpoints.MapStaffEndpoints();
                            endpoints.MapAuthEndpoints();
                        });
                    });
                });

            var host = hostBuilder.Build();
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<OokiGraderDbContext>();
                await db.Database.EnsureCreatedAsync();
                var now = DateTimeOffset.UtcNow;
                db.StaffUsers.Add(new StaffUserEntity
                {
                    Id = TestAuthenticationHandler.AdministratorId,
                    Username = "admin",
                    UsernameNormalized = "ADMIN",
                    DisplayName = "管理者",
                    PasswordHash = FakePasswordHasher.Encode(
                        "Administrator passphrase 2026"),
                    PasswordAlgorithm = "test",
                    PasswordAlgorithmVersion = 1,
                    Status = "active",
                    CredentialChangedAt = now,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                db.StaffUserRoles.Add(new StaffUserRoleEntity
                {
                    StaffUserId = TestAuthenticationHandler.AdministratorId,
                    RoleName = "administrator",
                    GrantedByStaffUserId =
                        TestAuthenticationHandler.AdministratorId,
                    GrantedAt = now,
                });
                await db.SaveChangesAsync();
            }

            await host.StartAsync();
            return new StaffTestApplication(host, connection);
        }

        public Task<HttpResponseMessage> GetStaffAsync(string staffId) =>
            SendAsync(
                HttpMethod.Get,
                $"/api/v1/staff/{staffId}",
                TestAuthenticationHandler.AdministratorId,
                "administrator");

        public async Task<HttpResponseMessage> SendAsync(
            HttpMethod method,
            string path,
            string staffId,
            string role,
            object? body = null,
            string? etag = null,
            string? sessionHash = null)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Add(TestAuthenticationHandler.StaffIdHeader, staffId);
            request.Headers.Add(TestAuthenticationHandler.RoleHeader, role);
            if (sessionHash is not null)
            {
                request.Headers.Add(
                    TestAuthenticationHandler.SessionHashHeader,
                    sessionHash);
            }

            if (etag is not null)
            {
                request.Headers.TryAddWithoutValidation("If-Match", etag);
            }

            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

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

        public async Task<LoginAttemptResult> LoginWithServiceAsync(
            string username,
            string password,
            IPAddress address)
        {
            await using var scope = _host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider
                .GetRequiredService<OokiGraderDbContext>();
            var service = new StaffAuthenticationService(
                db,
                scope.ServiceProvider.GetRequiredService<IPasswordHasher>(),
                scope.ServiceProvider.GetRequiredService<ISessionTokenService>(),
                TimeProvider.System,
                new ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Security:SessionIdleMinutes"] = "30",
                        ["Security:SessionAbsoluteHours"] = "12",
                    }).Build());
            return await service.LoginAsync(
                username,
                password,
                address,
                "integration-test",
                Guid.NewGuid().ToString("N"));
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.StopAsync();
            _host.Dispose();
            await _connection.DisposeAsync();
        }
    }

    private sealed class FakePasswordHasher : IPasswordHasher
    {
        public static string Encode(string password) => $"test:{password}";

        public Task<string> HashAsync(
            string password,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Encode(password));

        public Task<bool> VerifyAsync(
            string password,
            string encodedHash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(encodedHash == Encode(password));
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
        public const string SchemeName = "StaffIntegrationTest";
        public const string StaffIdHeader = "X-Test-Staff-Id";
        public const string RoleHeader = "X-Test-Role";
        public const string SessionHashHeader = "X-Test-Session-Hash";
        public const string AdministratorId = "01J00000000000000000000000";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var staffId = Request.Headers[StaffIdHeader].FirstOrDefault();
            var role = Request.Headers[RoleHeader].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(staffId)
                || string.IsNullOrWhiteSpace(role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, staffId),
                new(ClaimTypes.Name, "integration-staff"),
                new(ClaimTypes.Role, role),
                new(
                    "session_hash",
                    Request.Headers[SessionHashHeader].FirstOrDefault()
                    ?? "test-session"),
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
