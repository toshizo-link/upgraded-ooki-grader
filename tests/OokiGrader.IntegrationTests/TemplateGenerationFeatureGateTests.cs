using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OokiGrader.Host.Api;
using OokiGrader.Host.Services;

namespace OokiGrader.IntegrationTests;

public sealed class TemplateGenerationFeatureGateTests
{
    [Fact]
    public async Task DisabledFeatureRejectsCreateGenerateAndRetryAtTheApiBoundary()
    {
        await using var application = await DisabledGenerationApplication.CreateAsync();

        var responses = new[]
        {
            await application.Client.PostAsJsonAsync(
                "/api/v1/template-generation-batches/",
                new
                {
                    sourceId = "source-1",
                    testType = "hop",
                    subject = "算数",
                    answerStyle = (string?)null,
                    expectedSourceRowVersion = 1,
                }),
            await application.Client.PostAsJsonAsync(
                "/api/v1/template-generation-batches/batch-1/generate",
                new { expectedRowVersion = 1 }),
            await application.Client.PostAsJsonAsync(
                "/api/v1/template-generation-batches/batch-1/retry",
                new { expectedRowVersion = 1 }),
        };

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(
                "application/problem+json",
                response.Content.Headers.ContentType?.MediaType);
            using var problem = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync());
            Assert.Equal(
                "https://ooki-grader.local/problems/template-generation-disabled",
                problem.RootElement.GetProperty("type").GetString());
            Assert.Equal(
                "TEMPLATE_GENERATION_DISABLED",
                problem.RootElement.GetProperty("code").GetString());
        }
    }

    private sealed class DisabledGenerationApplication : IAsyncDisposable
    {
        private readonly IHost _host;

        private DisabledGenerationApplication(IHost host)
        {
            _host = host;
            Client = host.GetTestClient();
        }

        public HttpClient Client { get; }

        public static async Task<DisabledGenerationApplication> CreateAsync()
        {
            var hostBuilder = new HostBuilder()
                .ConfigureAppConfiguration(configuration =>
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["Features:Ai.TemplateGeneration"] = "false",
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
                        services.AddSingleton(
                            (TemplateGenerationBatchService)
                                RuntimeHelpers.GetUninitializedObject(
                                    typeof(TemplateGenerationBatchService)));
                        services.AddSingleton(
                            (TemplateGenerationFinalizationService)
                                RuntimeHelpers.GetUninitializedObject(
                                    typeof(TemplateGenerationFinalizationService)));
                    });
                    webBuilder.Configure(application =>
                    {
                        application.UseRouting();
                        application.UseAuthorization();
                        application.UseEndpoints(endpoints =>
                            endpoints.MapTemplateGenerationBatchEndpoints());
                    });
                });

            var host = hostBuilder.Build();
            await host.StartAsync();
            return new DisabledGenerationApplication(host);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.StopAsync();
            _host.Dispose();
        }
    }
}
