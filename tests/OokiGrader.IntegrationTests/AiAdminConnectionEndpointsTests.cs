using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
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
using OokiGrader.Ai.Abstractions;
using OokiGrader.Application.Abstractions;
using OokiGrader.Host.Api;
using OokiGrader.Host.Jobs;
using OokiGrader.Infrastructure.Persistence;

namespace OokiGrader.IntegrationTests;

public sealed class AiAdminConnectionEndpointsTests
{
    private const string GeminiModel = "gemini-3.5-flash-lite";
    private const string OpenRouterModel = "google/gemini-3.1-flash-lite";

    [Fact]
    public async Task GeminiAndOpenRouterCanCoexistButDuplicateProviderIsRejected()
    {
        await using var application = await AiAdminTestApplication.CreateAsync();

        var gemini = await application.PostAsync(
            "/api/v1/admin/ai-connections",
            ConnectionBody(
                "AIza-test-gemini-key-1234567890",
                AiProviders.GeminiDirect,
                GeminiModel));
        var openRouter = await application.PostAsync(
            "/api/v1/admin/ai-connections",
            ConnectionBody(
                "sk-or-test-openrouter-key-1234567890",
                AiProviders.OpenRouter,
                OpenRouterModel));
        var duplicate = await application.PostAsync(
            "/api/v1/admin/ai-connections",
            ConnectionBody(
                "sk-or-test-replacement-key-123456789",
                AiProviders.OpenRouter,
                "google/gemini-3.1-pro-preview"));

        Assert.Equal(HttpStatusCode.Created, gemini.StatusCode);
        Assert.Equal(HttpStatusCode.Created, openRouter.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("AI_CONNECTION_EXISTS", await ProblemCodeAsync(duplicate));

        var list = await application.GetAsync("/api/v1/admin/ai-connections");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var document = await ReadJsonAsync(list);
        var items = document.RootElement.GetProperty("items").EnumerateArray()
            .ToArray();
        Assert.Equal(2, items.Length);
        Assert.Contains(
            items,
            item => item.GetProperty("provider").GetString()
                == AiProviders.GeminiDirect
                && item.GetProperty("modelId").GetString() == GeminiModel);
        Assert.Contains(
            items,
            item => item.GetProperty("provider").GetString()
                == AiProviders.OpenRouter
                && item.GetProperty("modelId").GetString() == OpenRouterModel);
    }

    [Fact]
    public async Task ReplacingAConnectionKeepsProviderButAllowsModelUpdate()
    {
        await using var application = await AiAdminTestApplication.CreateAsync();
        var created = await application.PostAsync(
            "/api/v1/admin/ai-connections",
            ConnectionBody(
                "sk-or-test-openrouter-key-1234567890",
                AiProviders.OpenRouter,
                OpenRouterModel));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdDocument = await ReadJsonAsync(created);
        var createdRoot = createdDocument.RootElement;
        var connectionId = createdRoot.GetProperty("id").GetString();
        var revision = createdRoot.GetProperty("revision").GetInt64();
        Assert.False(string.IsNullOrWhiteSpace(connectionId));

        var providerChange = await application.PutAsync(
            $"/api/v1/admin/ai-connections/{connectionId}",
            ConnectionBody(
                "AIza-test-gemini-key-0987654321",
                AiProviders.GeminiDirect,
                GeminiModel,
                revision));
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            providerChange.StatusCode);
        Assert.Equal(
            "AI_CONNECTION_PROVIDER_IMMUTABLE",
            await ProblemCodeAsync(providerChange));

        const string updatedModel = "google/gemini-3.1-pro-preview";
        var modelChange = await application.PutAsync(
            $"/api/v1/admin/ai-connections/{connectionId}",
            ConnectionBody(
                "sk-or-test-new-openrouter-key-0987654321",
                AiProviders.OpenRouter,
                updatedModel,
                revision));
        Assert.Equal(HttpStatusCode.OK, modelChange.StatusCode);
        using var updatedDocument = await ReadJsonAsync(modelChange);
        Assert.Equal(
            AiProviders.OpenRouter,
            updatedDocument.RootElement.GetProperty("provider").GetString());
        Assert.Equal(
            updatedModel,
            updatedDocument.RootElement.GetProperty("modelId").GetString());
        Assert.Equal(
            "pending_probe",
            updatedDocument.RootElement.GetProperty("state").GetString());
        Assert.True(
            updatedDocument.RootElement.GetProperty("revision").GetInt64()
                > revision);
    }

    [Fact]
    public async Task ReplacingOpenRouterConnectionIsRejectedWhenFeatureIsDisabled()
    {
        await using var application = await AiAdminTestApplication.CreateAsync();
        var created = await application.PostAsync(
            "/api/v1/admin/ai-connections",
            ConnectionBody(
                "sk-or-test-openrouter-key-1234567890",
                AiProviders.OpenRouter,
                OpenRouterModel));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdDocument = await ReadJsonAsync(created);
        var connectionId = createdDocument.RootElement
            .GetProperty("id")
            .GetString();
        var revision = createdDocument.RootElement
            .GetProperty("revision")
            .GetInt64();
        application.DisableOpenRouter();

        var response = await application.PutAsync(
            $"/api/v1/admin/ai-connections/{connectionId}",
            ConnectionBody(
                "sk-or-test-new-openrouter-key-0987654321",
                AiProviders.OpenRouter,
                "google/gemini-3.1-pro-preview",
                revision));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            "AI_PROVIDER_FEATURE_DISABLED",
            await ProblemCodeAsync(response));
        await application.WithDatabaseAsync(async db =>
        {
            var connection = await db.AiConnections
                .AsNoTracking()
                .SingleAsync(item => item.Id == connectionId);
            Assert.Equal(OpenRouterModel, connection.ModelId);
            Assert.Equal(revision, connection.Revision);
            Assert.Equal(1, connection.CredentialRevision);
        });
    }

    [Fact]
    public async Task OpenRouterPricingUsesSelectedConnectionAndOfficialHost()
    {
        await using var application = await AiAdminTestApplication.CreateAsync();
        var created = await application.PostAsync(
            "/api/v1/admin/ai-connections",
            ConnectionBody(
                "sk-or-test-openrouter-key-1234567890",
                AiProviders.OpenRouter,
                OpenRouterModel));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var invalidSource = await application.PostAsync(
            "/api/v1/admin/pricing-snapshots",
            PricingBody("https://ai.google.dev/gemini-api/docs/pricing"));
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            invalidSource.StatusCode);
        Assert.Equal(
            "AI_PRICING_SNAPSHOT_INVALID",
            await ProblemCodeAsync(invalidSource));

        var saved = await application.PostAsync(
            "/api/v1/admin/pricing-snapshots",
            PricingBody("https://openrouter.ai/models"));
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        using var savedDocument = await ReadJsonAsync(saved);
        Assert.Equal(
            AiProviders.OpenRouter,
            savedDocument.RootElement.GetProperty("provider").GetString());
        Assert.Equal(
            OpenRouterModel,
            savedDocument.RootElement.GetProperty("modelId").GetString());

        var list = await application.GetAsync(
            "/api/v1/admin/pricing-snapshots");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listDocument = await ReadJsonAsync(list);
        var snapshot = Assert.Single(
            listDocument.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(
            AiProviders.OpenRouter,
            snapshot.GetProperty("provider").GetString());
        Assert.Equal(OpenRouterModel, snapshot.GetProperty("modelId").GetString());
    }

    [Fact]
    public async Task OpenRouterProbeWithoutImageSupportBlocksConnectionAndCreatesNoProfiles()
    {
        var openRouterProbe = new AiCapabilityProbeResult(
            Authentication: true,
            ModelAvailable: true,
            ImageInput: false,
            StructuredOutput: true,
            UsageMetadata: true,
            State: "passed",
            SafeErrorCode: null,
            Latency: TimeSpan.FromMilliseconds(25));
        await using var application = await AiAdminTestApplication.CreateAsync(
            openRouterProbe);
        var created = await application.PostAsync(
            "/api/v1/admin/ai-connections",
            ConnectionBody(
                "sk-or-test-openrouter-key-1234567890",
                AiProviders.OpenRouter,
                OpenRouterModel));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdDocument = await ReadJsonAsync(created);
        var connectionId = createdDocument.RootElement
            .GetProperty("id")
            .GetString();

        var probe = await application.PostAsync(
            $"/api/v1/admin/ai-connections/{connectionId}:test");

        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
        using var probeDocument = await ReadJsonAsync(probe);
        Assert.Equal(
            "passed",
            probeDocument.RootElement.GetProperty("state").GetString());
        Assert.False(
            probeDocument.RootElement.GetProperty("imageInput").GetBoolean());
        Assert.Equal(1, application.OpenRouterClient.ProbeCount);
        Assert.Equal(
            AiProviderCatalog.OpenRouterBaseAddress,
            application.OpenRouterClient.LastConnection?.BaseAddress);

        await application.WithDatabaseAsync(async db =>
        {
            var connection = await db.AiConnections
                .AsNoTracking()
                .SingleAsync(item => item.Id == connectionId);
            Assert.Equal(AiProviders.OpenRouter, connection.Provider);
            Assert.Equal("blocked", connection.State);
            Assert.Equal("passed", connection.LastCapabilityProbeState);
            Assert.Equal(1, await db.AiCapabilityProbes.CountAsync());
            Assert.Equal(0, await db.AiTaskProfiles.CountAsync());
        });
    }

    private static object ConnectionBody(
        string apiKey,
        string provider,
        string modelId,
        long? revision = null) => new
        {
            apiKey,
            provider,
            modelId,
            timeoutSeconds = 75,
            concurrencyLimit = 2,
            revision,
        };

    private static object PricingBody(string sourceUrl) => new
    {
        provider = AiProviders.OpenRouter,
        modelId = OpenRouterModel,
        inputUsdMicrosPerMillionTokens = 250_000,
        outputUsdMicrosPerMillionTokens = 1_500_000,
        thinkingUsdMicrosPerMillionTokens = 0,
        sourceUrl,
    };

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private static async Task<string?> ProblemCodeAsync(
        HttpResponseMessage response)
    {
        using var document = await ReadJsonAsync(response);
        return document.RootElement.GetProperty("code").GetString();
    }

    private sealed class AiAdminTestApplication : IAsyncDisposable
    {
        private static readonly DateTimeOffset UtcNow = new(
            2026,
            8,
            6,
            4,
            0,
            0,
            TimeSpan.Zero);

        private readonly IHost _host;
        private readonly SqliteConnection _connection;
        private readonly TestProviderFeaturePolicy _featurePolicy;

        private AiAdminTestApplication(
            IHost host,
            SqliteConnection connection,
            ProbeProviderClient openRouterClient,
            TestProviderFeaturePolicy featurePolicy)
        {
            _host = host;
            _connection = connection;
            _featurePolicy = featurePolicy;
            OpenRouterClient = openRouterClient;
            Client = host.GetTestClient();
            Client.Timeout = TimeSpan.FromSeconds(5);
        }

        private HttpClient Client { get; }
        public ProbeProviderClient OpenRouterClient { get; }

        public static async Task<AiAdminTestApplication> CreateAsync(
            AiCapabilityProbeResult? openRouterProbe = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var geminiClient = new ProbeProviderClient(
                AiProviders.GeminiDirect,
                PassedProbe());
            var openRouterClient = new ProbeProviderClient(
                AiProviders.OpenRouter,
                openRouterProbe ?? PassedProbe());
            var secretStore = new InMemorySecretStore();
            var featurePolicy = new TestProviderFeaturePolicy();

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
                        services.AddSingleton<TimeProvider>(
                            new FixedTimeProvider(UtcNow));
                        services.AddDbContext<OokiGraderDbContext>(
                            options => options.UseSqlite(connection));
                        services.AddSingleton<IAiSecretStore>(secretStore);
                        services.AddSingleton<IAiProviderClient>(geminiClient);
                        services.AddSingleton<IAiProviderClient>(openRouterClient);
                        services.AddSingleton<IAiProviderClientResolver>(provider =>
                            new AiProviderClientResolver(
                                provider.GetServices<IAiProviderClient>()));
                        services.AddSingleton<IAiProviderFeaturePolicy>(
                            featurePolicy);
                        services.AddSingleton<IAiPromptBundleCatalog>(
                            new StubPromptBundleCatalog());
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
                await db.Database.EnsureCreatedAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }

            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
            return new AiAdminTestApplication(
                host,
                connection,
                openRouterClient,
                featurePolicy);
        }

        public void DisableOpenRouter() =>
            _featurePolicy.OpenRouterEnabled = false;

        public Task<HttpResponseMessage> GetAsync(string path) =>
            SendAsync(HttpMethod.Get, path);

        public Task<HttpResponseMessage> PostAsync(
            string path,
            object? body = null) => SendAsync(HttpMethod.Post, path, body);

        public Task<HttpResponseMessage> PutAsync(
            string path,
            object body) => SendAsync(HttpMethod.Put, path, body);

        public async Task WithDatabaseAsync(
            Func<OokiGraderDbContext, Task> action)
        {
            await using var scope = _host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider
                .GetRequiredService<OokiGraderDbContext>();
            await action(db);
        }

        private async Task<HttpResponseMessage> SendAsync(
            HttpMethod method,
            string path,
            object? body = null)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Add(
                TestAuthenticationHandler.RoleHeader,
                "administrator");
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            return await Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.StopAsync();
            _host.Dispose();
            await _connection.DisposeAsync();
        }

        private static AiCapabilityProbeResult PassedProbe() => new(
            Authentication: true,
            ModelAvailable: true,
            ImageInput: true,
            StructuredOutput: true,
            UsageMetadata: true,
            State: "passed",
            SafeErrorCode: null,
            Latency: TimeSpan.FromMilliseconds(10));
    }

    public sealed class ProbeProviderClient(
        string provider,
        AiCapabilityProbeResult result) : IAiProviderClient
    {
        private int _probeCount;

        public string Provider { get; } = provider;
        public int ProbeCount => Volatile.Read(ref _probeCount);
        public AiConnectionSettings? LastConnection { get; private set; }

        public Task<AiProviderResponse> GenerateAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            AiProviderRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AiCapabilityProbeResult> ProbeAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            CancellationToken cancellationToken = default)
        {
            LastConnection = connection;
            Interlocked.Increment(ref _probeCount);
            return Task.FromResult(result);
        }
    }

    private sealed class InMemorySecretStore : IAiSecretStore
    {
        private readonly ConcurrentDictionary<string, byte[]> _secrets = new();

        public Task<AiSecretReference> WriteAsync(
            string ownerId,
            long credentialRevision,
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken = default)
        {
            var reference = new AiSecretReference(
                $"test-secret:{ownerId}:{credentialRevision}");
            _secrets[reference.Value] = Encoding.UTF8.GetBytes(secret.ToString());
            return Task.FromResult(reference);
        }

        public Task<AiSecretLease> ReadAsync(
            AiSecretReference reference,
            CancellationToken cancellationToken = default)
        {
            if (!_secrets.TryGetValue(reference.Value, out var bytes))
            {
                throw new InvalidOperationException("Test secret was not found.");
            }

            return Task.FromResult(AiSecretLease.CopyFrom(bytes));
        }

        public Task<bool> DeleteAsync(
            AiSecretReference reference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_secrets.TryRemove(reference.Value, out _));
    }

    private sealed class StubPromptBundleCatalog : IAiPromptBundleCatalog
    {
        private static readonly JsonElement Schema =
            JsonSerializer.Deserialize<JsonElement>("{\"type\":\"object\"}");

        public AiPromptBundle GetRequired(string taskType) => new(
            taskType,
            "test-prompt-v1",
            "test-schema-v1",
            "Test prompt",
            Schema,
            new string('a', 64));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class TestProviderFeaturePolicy : IAiProviderFeaturePolicy
    {
        public bool OpenRouterEnabled { get; set; } = true;

        public bool IsEnabled(string provider) => provider switch
        {
            AiProviders.GeminiDirect => true,
            AiProviders.OpenRouter => OpenRouterEnabled,
            _ => false,
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
        public const string SchemeName = "AiAdminConnectionIntegrationTest";
        public const string RoleHeader = "X-Test-Role";
        private const string AdministratorId =
            "01J00000000000000000000000";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers[RoleHeader].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, AdministratorId),
                    new Claim(ClaimTypes.Name, "ai-admin-test-user"),
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
