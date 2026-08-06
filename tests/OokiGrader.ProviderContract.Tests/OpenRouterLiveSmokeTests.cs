using System.Security.Cryptography;
using System.Text;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Ai.OpenRouter;

namespace OokiGrader.ProviderContract.Tests;

public sealed class OpenRouterLiveSmokeTests
{
    [LiveOpenRouterFact("OPENROUTER_API_KEY", "OOKI_OPENROUTER_MODEL_ID")]
    public async Task ConfiguredModelReportsStructuredImageCapability()
    {
        var credentialValue = Environment.GetEnvironmentVariable(
            "OPENROUTER_API_KEY");
        var modelId = Environment.GetEnvironmentVariable(
            "OOKI_OPENROUTER_MODEL_ID");
        if (string.IsNullOrWhiteSpace(credentialValue)
            || string.IsNullOrWhiteSpace(modelId))
        {
            throw new InvalidOperationException(
                "The live OpenRouter test requires explicit credentials and a model.");
        }

        modelId = modelId.Trim();
        Assert.True(AiProviderCatalog.IsModelIdValid(
            AiProviders.OpenRouter,
            modelId));
        var credential = Encoding.ASCII.GetBytes(credentialValue);
        try
        {
            using var httpClient = new HttpClient
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            var client = new OpenRouterClient(httpClient);
            var result = await client.ProbeAsync(
                new AiConnectionSettings(
                    "live-openrouter-smoke",
                    AiProviders.OpenRouter,
                    AiProviderCatalog.OpenRouterBaseAddress,
                    modelId,
                    TimeSpan.FromMinutes(2)),
                credential);

            Assert.True(result.Authentication);
            Assert.True(result.ModelAvailable);
            Assert.True(result.StructuredOutput);
            Assert.True(result.UsageMetadata);
            if (AiProviderCatalog.IsDeepSeekV4FlashFamily(modelId))
            {
                Assert.False(result.ImageInput);
                Assert.Equal("failed", result.State);
                Assert.Equal(
                    "openrouter_image_not_supported",
                    result.SafeErrorCode);
            }
            else
            {
                Assert.True(
                    result.State == "passed",
                    $"OpenRouter probe failed with safe code: {result.SafeErrorCode}");
                Assert.True(result.ImageInput);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credential);
        }
    }

    private sealed class LiveOpenRouterFactAttribute : FactAttribute
    {
        public LiveOpenRouterFactAttribute(params string[] requiredVariables)
        {
            var missing = requiredVariables
                .Where(variable =>
                    string.IsNullOrWhiteSpace(
                        Environment.GetEnvironmentVariable(variable)))
                .ToArray();
            if (missing.Length > 0)
            {
                Skip = "Live OpenRouter smoke test requires: "
                    + string.Join(", ", missing);
            }
        }
    }
}
