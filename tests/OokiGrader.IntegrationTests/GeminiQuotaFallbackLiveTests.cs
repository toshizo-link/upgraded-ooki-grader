using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Ai.Gemini;
using OokiGrader.Application.Abstractions;
using OokiGrader.Host.Jobs;
using OokiGrader.Infrastructure.Security;

namespace OokiGrader.IntegrationTests;

/// <summary>
/// Opt-in: the primary HTTP 429 is simulated without exhausting Google's quota;
/// the unchanged production grading request is sent to the REAL Lite endpoint.
/// The API key is read from an existing DPAPI store and is never exported.
/// </summary>
public sealed class GeminiQuotaFallbackLiveTests
{
    [LiveRoutingFact]
    [Trait("Category", "Live")]
    public async Task QuotaRejectedGradingUsesRealLiteAndReturnsCorrectScores()
    {
        var secretStore = new WindowsDpapiAiSecretStore(new WindowsDpapiAiSecretStoreOptions
        { RootPath = Environment.GetEnvironmentVariable("OOKI_ROUTING_SECRET_ROOT")! });
        using var secret = await secretStore.ReadAsync(new AiSecretReference(
            Environment.GetEnvironmentVariable("OOKI_ROUTING_SECRET_REFERENCE")!));
        var image = await File.ReadAllBytesAsync(Environment.GetEnvironmentVariable("OOKI_GEMINI_FEATURE_IMAGE")!);
        var directory = Path.Combine(Path.GetTempPath(), "ooki-live-routing-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var handler = new QuotaHandler();
            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            var client = new GeminiTaskRoutingClient(new GeminiDirectClient(http),
                new GeminiQuotaCooldownStore(directory, TimeProvider.System));
            using var catalog = new ApprovedPromptBundleCatalog();
            var bundle = catalog.GetRequired(AiTaskTypes.InitialGrading);
            const string key = "live-quota-fallback-3-items";
            var questions = new[]
            {
                new { Id = "q1", Label = "1", Text = "日本の首都を漢字で書きなさい。", Answer = "東京", Points = 8_000 },
                new { Id = "q2", Label = "2", Text = "ASEAN（アセアン）を日本語で何というか。", Answer = "東南アジア諸国連合", Points = 10_000 },
                new { Id = "q3", Label = "3", Text = "インドで最も多くの人が信仰している宗教を書きなさい。", Answer = "ヒンドゥー教", Points = 8_000 },
            };
            var instruction = "Inspect the original completed Japanese test image. Transcribe visible answers exactly. "
                + "Grade only against teacher-approved accepted answers. Include every requested question exactly once; "
                + "do not infer invisible answers. Transcribe the visible identity on PAGE_1. "
                + JsonSerializer.Serialize(new
                {
                    schema_version = bundle.SchemaVersion, request_key = key,
                    chunk_index = 0, chunk_count = 1, identity_required = true,
                    media = new[] { new { media_index = 0, page_number = 1, page_label = "PAGE_1" } },
                    questions = questions.Select(q => new
                    {
                        question_id = q.Id, display_label = q.Label, question_text = q.Text,
                        question_type = "exact_short_text", grading_mode = "transcribe_then_rules",
                        maximum_points_milli = q.Points, point_increment_milli = 1_000,
                        allow_non_kanji = false, requires_complete_answer = true,
                        answer_order_insensitive = false, rubric_text = (string?)null,
                        accepted_answers = new[] { q.Answer },
                    }),
                });
            var response = await client.GenerateAsync(new AiConnectionSettings("live-routing",
                AiProviders.GeminiDirect, AiProviderCatalog.GeminiBaseAddress,
                AiProviderCatalog.GeminiDefaultModelId, TimeSpan.FromMinutes(5)), secret.Utf8Bytes,
                new AiProviderRequest(key, bundle.TaskType, bundle.PromptVersion, bundle.SchemaVersion,
                    bundle.SystemInstruction, instruction, bundle.ResponseJsonSchema,
                    [new AiMediaPart("image/png", image, Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant())],
                    ThinkingLevel: "LOW"));
            Assert.True(AiResponseMetadataValidator.IsAccepted(response));
            Assert.Equal(GeminiTaskRoutingClient.QuotaReason, response.ModelRoutingReason);
            Assert.Equal(AiProviderCatalog.GeminiLightModelId, response.ActualModel);
            Assert.Equal(1, handler.PrimaryAttempts);
            Assert.Equal(1, handler.LiteAttempts);
            var root = response.StructuredOutput;
            Assert.Equal(key, root.GetProperty("request_key").GetString());
            Assert.Equal(bundle.SchemaVersion, root.GetProperty("schema_version").GetString());
            Assert.Empty(root.GetProperty("missing_question_ids").EnumerateArray());
            Assert.Equal(3, root.GetProperty("results").GetArrayLength());
            foreach (var q in questions)
            {
                var result = Assert.Single(root.GetProperty("results").EnumerateArray(),
                    item => item.GetProperty("question_id").GetString() == q.Id);
                Assert.Equal(q.Answer, result.GetProperty("transcription").GetString());
                Assert.Equal(q.Points, result.GetProperty("proposed_points_milli").GetInt32());
                Assert.Equal("correct", result.GetProperty("proposed_outcome").GetString());
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(image);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class QuotaHandler() : DelegatingHandler(new HttpClientHandler())
    {
        public int PrimaryAttempts { get; private set; }
        public int LiteAttempts { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath.Contains("/models/gemini-3.8-flash:"))
            {
                PrimaryAttempts++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                { Content = new StringContent("""{"error":{"details":[{"retryDelay":"60s"}]}}""") });
            }
            if (!request.RequestUri.AbsolutePath.Contains("/models/gemini-3.5-flash-lite:"))
                throw new InvalidOperationException("Unexpected live endpoint.");
            LiteAttempts++;
            return base.SendAsync(request, token);
        }
    }

    private sealed class LiveRoutingFactAttribute : FactAttribute
    {
        public LiveRoutingFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OOKI_ROUTING_SECRET_ROOT"))
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OOKI_ROUTING_SECRET_REFERENCE"))
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OOKI_GEMINI_FEATURE_IMAGE")))
                Skip = "Opt-in live inputs are required.";
        }
    }
}
