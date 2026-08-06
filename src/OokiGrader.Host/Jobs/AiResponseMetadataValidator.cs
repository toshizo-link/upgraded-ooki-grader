using OokiGrader.Ai.Abstractions;
using OokiGrader.Ai.Gemini;

namespace OokiGrader.Host.Jobs;

/// <summary>
/// Rejects provider responses whose routing or completion metadata does not
/// match the approved profile. Google can report a concrete numeric revision
/// of a stable model alias; OpenRouter must report the exact selected slug so
/// an unapproved cross-model fallback is never accepted.
/// </summary>
internal static class AiResponseMetadataValidator
{
    public const string InvalidMetadataErrorCode =
        "ai_response_metadata_invalid";

    public static bool IsAccepted(
        AiProviderResponse response,
        string selectedModel = GeminiBatchClient.SelectedModel)
        => IsAccepted(response, AiProviders.GeminiDirect, selectedModel);

    public static bool IsAccepted(
        AiProviderResponse response,
        string selectedProvider,
        string selectedModel)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.Provider == selectedProvider
            && response.RequestedModel == selectedModel
            && IsAcceptedActualModel(
                response.ActualModel,
                selectedProvider,
                selectedModel)
            && IsSuccessfulFinishReason(
                selectedProvider,
                response.FinishReason);
    }

    public static void Validate(
        AiProviderResponse response,
        string selectedModel = GeminiBatchClient.SelectedModel)
        => Validate(response, AiProviders.GeminiDirect, selectedModel);

    public static void Validate(
        AiProviderResponse response,
        string selectedProvider,
        string selectedModel)
    {
        if (!IsAccepted(response, selectedProvider, selectedModel))
        {
            throw new InvalidDataException(InvalidMetadataErrorCode);
        }
    }

    private static bool IsAcceptedActualModel(
        string? actualModel,
        string selectedProvider,
        string selectedModel)
    {
        if (selectedProvider == AiProviders.OpenRouter)
        {
            return actualModel == selectedModel;
        }

        if (actualModel is null || actualModel == selectedModel)
        {
            return true;
        }

        var prefix = selectedModel + "-";
        return actualModel.StartsWith(prefix, StringComparison.Ordinal)
            && actualModel.Length > prefix.Length
            && actualModel.AsSpan(prefix.Length).IndexOfAnyExceptInRange(
                '0',
                '9') < 0;
    }

    private static bool IsSuccessfulFinishReason(
        string selectedProvider,
        string finishReason) =>
        finishReason == "STOP"
        || (selectedProvider == AiProviders.OpenRouter
            && finishReason == "stop");
}
