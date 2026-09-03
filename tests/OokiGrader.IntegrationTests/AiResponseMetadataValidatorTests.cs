using System.Text.Json;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Host.Jobs;

namespace OokiGrader.IntegrationTests;

public sealed class AiResponseMetadataValidatorTests
{
    private const string HistoricalGeminiModel =
        "gemini-3.5-flash-lite";

    [Theory]
    [InlineData(null)]
    [InlineData(AiProviderCatalog.GeminiDefaultModelId)]
    [InlineData(AiProviderCatalog.GeminiDefaultModelId + "-001")]
    [InlineData(AiProviderCatalog.GeminiDefaultModelId + "-20260721")]
    public void AcceptsSelectedAliasAndNumericProviderRevisions(
        string? actualModel)
    {
        Assert.True(AiResponseMetadataValidator.IsAccepted(
            Response(actualModel)));
    }

    [Theory]
    [InlineData(AiProviderCatalog.GeminiDefaultModelId + "-preview")]
    [InlineData(HistoricalGeminiModel)]
    [InlineData(AiProviderCatalog.GeminiDefaultModelId + "-001-extra")]
    public void RejectsUnexpectedActualModels(string actualModel)
    {
        Assert.False(AiResponseMetadataValidator.IsAccepted(
            Response(actualModel)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(HistoricalGeminiModel)]
    [InlineData(HistoricalGeminiModel + "-001")]
    [InlineData(HistoricalGeminiModel + "-20260721")]
    public void AcceptsHistoricalAliasWhenItWasTheSelectedModel(
        string? actualModel)
    {
        Assert.True(AiResponseMetadataValidator.IsAccepted(
            Response(actualModel, HistoricalGeminiModel),
            AiProviders.GeminiDirect,
            HistoricalGeminiModel));
    }

    [Fact]
    public void RejectsProviderRequestedModelAndFinishReasonDrift()
    {
        Assert.False(AiResponseMetadataValidator.IsAccepted(
            Response() with { Provider = "other" }));
        Assert.False(AiResponseMetadataValidator.IsAccepted(
            Response() with { RequestedModel = "other" }));
        Assert.False(AiResponseMetadataValidator.IsAccepted(
            Response() with { FinishReason = "MAX_TOKENS" }));
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("STOP")]
    public void AcceptsOpenRouterOnlyForTheExactSelectedModel(
        string finishReason)
    {
        const string model = "google/gemini-3.1-flash-lite";

        Assert.True(AiResponseMetadataValidator.IsAccepted(
            OpenRouterResponse(model, model, finishReason),
            AiProviders.OpenRouter,
            model));
    }

    [Theory]
    [InlineData("google/gemini-3.1-flash-lite-001")]
    [InlineData("google/gemini-3.1-pro")]
    [InlineData(null)]
    public void RejectsOpenRouterActualModelDrift(string? actualModel)
    {
        const string model = "google/gemini-3.1-flash-lite";

        Assert.False(AiResponseMetadataValidator.IsAccepted(
            OpenRouterResponse(model, actualModel, "stop"),
            AiProviders.OpenRouter,
            model));
    }

    [Fact]
    public void RejectsOpenRouterProviderRequestedModelAndFinishReasonDrift()
    {
        const string model = "google/gemini-3.1-flash-lite";
        var response = OpenRouterResponse(model, model, "stop");

        Assert.False(AiResponseMetadataValidator.IsAccepted(
            response with { Provider = AiProviders.GeminiDirect },
            AiProviders.OpenRouter,
            model));
        Assert.False(AiResponseMetadataValidator.IsAccepted(
            response with { RequestedModel = "google/gemini-3.1-pro" },
            AiProviders.OpenRouter,
            model));
        Assert.False(AiResponseMetadataValidator.IsAccepted(
            response with { FinishReason = "length" },
            AiProviders.OpenRouter,
            model));
    }

    private static AiProviderResponse Response(
        string? actualModel = AiProviderCatalog.GeminiDefaultModelId,
        string requestedModel = AiProviderCatalog.GeminiDefaultModelId)
    {
        using var document = JsonDocument.Parse("""{"ok":true}""");
        return new AiProviderResponse(
            AiProviders.GeminiDirect,
            requestedModel,
            actualModel,
            "response-id",
            "STOP",
            document.RootElement.Clone(),
            new AiUsage(1, 0, 1, 0, 2),
            TimeSpan.FromMilliseconds(10));
    }

    private static AiProviderResponse OpenRouterResponse(
        string requestedModel,
        string? actualModel,
        string finishReason)
    {
        using var document = JsonDocument.Parse("""{"ok":true}""");
        return new AiProviderResponse(
            AiProviders.OpenRouter,
            requestedModel,
            actualModel,
            "openrouter-response-id",
            finishReason,
            document.RootElement.Clone(),
            new AiUsage(1, 0, 1, 0, 2),
            TimeSpan.FromMilliseconds(10));
    }
}
