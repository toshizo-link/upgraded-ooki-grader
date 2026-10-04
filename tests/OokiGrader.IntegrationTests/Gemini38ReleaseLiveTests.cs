using System.Security.Cryptography;
using System.Text;
using System.Net;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Ai.Gemini;
using OokiGrader.Domain.Templates;
using OokiGrader.Host.Common;
using OokiGrader.Host.Jobs;
using OokiGrader.Host.Services;
using OokiGrader.Preprocessing;
using OokiGrader.Application.Abstractions;
using OokiGrader.Infrastructure.Security;
using Xunit.Abstractions;

namespace OokiGrader.IntegrationTests;

/// <summary>
/// Opt-in release acceptance against the actual provider, production schema,
/// PDF preprocessing and response validator. Credentials and source documents
/// are supplied at run time and are never persisted by this test.
/// </summary>
public sealed class Gemini38ReleaseLiveTests(ITestOutputHelper output)
{
    [LiveReleaseFact(requiresPdf: false)]
    [Trait("Category", "Live")]
    public async Task ExactGemini38PassesProductionImageCapabilityProbe()
    {
        var key = await ReadCredentialAsync();
        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var probe = await new GeminiDirectClient(http).ProbeAsync(
                new AiConnectionSettings("release-probe", AiProviders.GeminiDirect,
                    AiProviderCatalog.GeminiBaseAddress, "gemini-3.8-flash",
                    TimeSpan.FromMinutes(5)), key);
            Assert.True(probe.State == "passed",
                $"Capability probe failed: {probe.SafeErrorCode}");
            Assert.True(probe.ImageInput && probe.StructuredOutput && probe.UsageMetadata);
            output.WriteLine("Model=gemini-3.8-flash; productionCapabilityProbe=passed");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    [LiveReleaseFact]
    [Trait("Category", "Live")]
    public async Task ProductionFillBlankPdfExtractsWithGemini38AndPassesValidation()
    {
        var key = await ReadCredentialAsync();
        var path = Environment.GetEnvironmentVariable("OOKI_RELEASE_TEST_PDF")!;
        var routingDirectory = Path.Combine(Path.GetTempPath(), "ooki-live-pdf-routing-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var source = File.OpenRead(path);
            var pageCount = await new LocalPdfPageCountReader()
                .GetPageCountAsync(source, 50);
            Assert.InRange(pageCount, 1, 50);
            source.Position = 0;
            var media = await new PdfPageRangeExtractor(new PreprocessingService())
                .ExtractAsync(source, "acceptance.pdf", 1, pageCount,
                    new Dictionary<int, int>());
            using var catalog = new ApprovedPromptBundleCatalog();
            var bundle = catalog.GetRequired(AiTaskTypes.TemplateExtraction);
            const string requestKey = "release-0.9.15-fill-blank";
            const string unitId = "release-acceptance-unit";
            var profile = new TemplateGenerationProfile(
                TemplateGenerationProfile.CurrentProfileVersion,
                TestType.Other, "理科", AnswerStyle.FillBlank,
                TemplatePromptSystem.FillBlank, pageCount, 1, 1, pageCount,
                null, null, null,
                TemplateGenerationProfile.CurrentSplitPolicyVersion,
                TemplateGenerationProfile.CurrentNamingPolicyVersion,
                TemplateGenerationBatchService.ExtractionPromptVersion,
                TemplateGenerationBatchService.ExtractionSchemaVersion);
            var instruction = TemplateExtractionInstructionBuilder.Build(
                requestKey, unitId, profile, rotationsWereApplied: false);
            var connection = new AiConnectionSettings(
                "release-acceptance", AiProviders.GeminiDirect,
                AiProviderCatalog.GeminiBaseAddress,
                Environment.GetEnvironmentVariable("OOKI_RELEASE_MODEL") ?? "gemini-3.8-flash",
                TimeSpan.FromMinutes(5));
            var injectedFailures = int.TryParse(Environment.GetEnvironmentVariable("OOKI_RELEASE_INJECT_503"),
                out var injected) ? injected : 0;
            Assert.InRange(injectedFailures, 0, 3);
            var injectQuota = Environment.GetEnvironmentVariable("OOKI_RELEASE_INJECT_429") == "1";
            using var handler = new ServiceUnavailableInjectionHandler(injectedFailures, injectQuota);
            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            var client = new GeminiTaskRoutingClient(new GeminiDirectClient(http),
                new GeminiQuotaCooldownStore(routingDirectory, TimeProvider.System));
            IReadOnlyList<AiMediaPart> providerMedia =
                [new AiMediaPart("application/pdf", media.Bytes, media.Sha256)];
            if (Environment.GetEnvironmentVariable("OOKI_RELEASE_RASTER_PDF") == "1")
            {
                await using var rasterSource = new MemoryStream(media.Bytes, writable: false);
                var raster = await new PreprocessingService().ProcessAsync(rasterSource,
                    new PreprocessingInput("application/pdf", "acceptance.pdf", MaximumPages: 50));
                providerMedia = raster.Pages.OrderBy(page => page.PageNumber).Select(page =>
                    new AiMediaPart("image/png", page.NormalizedPng.Bytes,
                        page.NormalizedPng.Sha256)).ToArray();
                output.WriteLine("DiagnosticMedia=ordered-pdf-page-images");
            }
            output.WriteLine("Phase=production-template-extraction");
            var response = await client.GenerateAsync(connection, key,
                new AiProviderRequest(requestKey, bundle.TaskType,
                    bundle.PromptVersion, bundle.SchemaVersion,
                    bundle.SystemInstruction, instruction.UserInstruction,
                    bundle.ResponseJsonSchema,
                    providerMedia,
                    MaxOutputTokens: 65_536,
                    ThinkingLevel: Environment.GetEnvironmentVariable("OOKI_RELEASE_THINKING") ?? "MEDIUM"));
            AiResponseMetadataValidator.Validate(response, connection.ModelId);
            // Optional private diagnostic artifact, never part of a release.
            if (Environment.GetEnvironmentVariable("OOKI_RELEASE_RESPONSE_PATH") is { Length: > 0 } responsePath)
                await File.WriteAllTextAsync(responsePath, response.StructuredOutput.GetRawText());
            var extraction = OrientationGatedTemplateExtractionValidator.Validate(
                response.StructuredOutput, requestKey, instruction.Pages,
                new Dictionary<string, TemplateExtractionSourceEvidence>
                {
                    [unitId] = new(unitId, "unit_test_paper", pageCount),
                }, defaultPointsMilli: 1_000, targetTotalPointsMilli: null);
            Assert.Equal(TemplateExtractionAction.Extract, extraction.Action);
            Assert.NotNull(extraction.Extraction);
            var count = extraction.Extraction.Pages.Sum(page => page.Questions.Count);
            Assert.True(count > 0, "No scoring questions were extracted.");
            if (int.TryParse(Environment.GetEnvironmentVariable("OOKI_RELEASE_EXPECTED_BLANKS"),
                    out var expectedCount))
            {
                Assert.Equal(expectedCount, count);
            }
            Assert.DoesNotContain(extraction.Extraction.ReviewIssues,
                issue => issue.Blocking);
            if (Environment.GetEnvironmentVariable("OOKI_RELEASE_EXPECTED_ANSWERS_PATH") is { Length: > 0 } expectedPath)
            {
                var expected = System.Text.Json.JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(expectedPath))!;
                var actual = extraction.Extraction.Pages.SelectMany(page => page.Questions)
                    .Select(question => question.ExpectedAnswer).ToArray();
                Assert.Equal(expected, actual);
                output.WriteLine($"IndependentlyReviewedModelAnswers={expected.Length}; orderAndScript=passed");
            }
            Assert.Equal(injectedFailures, handler.InjectedCount);
            if (injectedFailures == 3 || injectQuota)
            {
                Assert.Equal(AiProviderCatalog.GeminiLightModelId, response.ActualModel);
                Assert.Equal(injectQuota ? GeminiTaskRoutingClient.QuotaReason : GeminiTaskRoutingClient.ServiceReason,
                    response.ModelRoutingReason);
            }
            output.WriteLine($"Model={connection.ModelId}; pages={pageCount}; questions={count}; "
                + $"finish={response.FinishReason}; totalTokens={response.Usage.TotalTokens}; "
                + $"actualModel={response.ActualModel}; route={response.ModelRoutingReason}; "
                + $"injected503={handler.InjectedCount}; injected429={handler.QuotaCount}; productionValidation=passed");
            CryptographicOperations.ZeroMemory(media.Bytes);
        }
        catch (AiProviderException exception)
        {
            Assert.Fail($"Provider failure: {exception.Kind}; {exception.SafeErrorCode}; HTTP={exception.HttpStatusCode}");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (Directory.Exists(routingDirectory)) Directory.Delete(routingDirectory, true);
        }
    }

    private sealed class LiveReleaseFactAttribute : FactAttribute
    {
        public LiveReleaseFactAttribute(bool requiresPdf = true)
        {
            if ((string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OOKI_GEMINI_API_KEY"))
                    && (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OOKI_ROUTING_SECRET_ROOT"))
                        || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OOKI_ROUTING_SECRET_REFERENCE"))))
                || (requiresPdf && string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("OOKI_RELEASE_TEST_PDF"))))
            {
                Skip = "Requires OOKI_GEMINI_API_KEY and OOKI_RELEASE_TEST_PDF.";
            }
        }
    }

    private static async Task<byte[]> ReadCredentialAsync()
    {
        if (Environment.GetEnvironmentVariable("OOKI_GEMINI_API_KEY") is { Length: > 0 } value)
            return Encoding.UTF8.GetBytes(value);
        var store = new WindowsDpapiAiSecretStore(new WindowsDpapiAiSecretStoreOptions
        { RootPath = Environment.GetEnvironmentVariable("OOKI_ROUTING_SECRET_ROOT")! });
        using var secret = await store.ReadAsync(new AiSecretReference(
            Environment.GetEnvironmentVariable("OOKI_ROUTING_SECRET_REFERENCE")!));
        return secret.Utf8Bytes.ToArray();
    }

    private sealed class ServiceUnavailableInjectionHandler(int failureCount, bool injectQuota)
        : DelegatingHandler(new HttpClientHandler())
    {
        public int InjectedCount { get; private set; }
        public int QuotaCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var primary = request.RequestUri!.AbsolutePath.Contains("/models/gemini-3.8-flash:");
            if (primary && injectQuota)
            {
                QuotaCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
            }
            if (primary && InjectedCount < failureCount)
            {
                InjectedCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }
            return base.SendAsync(request, token);
        }
    }
}
