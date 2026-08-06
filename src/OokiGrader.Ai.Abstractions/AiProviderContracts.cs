using System.Text.Json;
using System.Text.RegularExpressions;

namespace OokiGrader.Ai.Abstractions;

public static class AiProviders
{
    public const string GeminiDirect = "geminiDirect";
    public const string OpenRouter = "openRouter";
}

/// <summary>
/// Canonical provider endpoint and model-shape policy shared by configuration,
/// workers, and provider adapters. Inference hosts are code-owned allowlist
/// entries; administrators configure only the model identifier and credential.
/// </summary>
public static partial class AiProviderCatalog
{
    public const string GeminiEndpointProfile = "googleGenerativeLanguage";
    public const string OpenRouterEndpointProfile = "openRouterChatCompletions";
    public const string DeepSeekV4FlashModelId =
        "deepseek/deepseek-v4-flash";

    public static readonly Uri GeminiBaseAddress =
        new("https://generativelanguage.googleapis.com/");

    public static readonly Uri OpenRouterBaseAddress =
        new("https://openrouter.ai/api/v1/");

    public static bool IsSupportedProvider(string provider) =>
        provider is AiProviders.GeminiDirect or AiProviders.OpenRouter;

    public static Uri GetBaseAddress(string provider) => provider switch
    {
        AiProviders.GeminiDirect => GeminiBaseAddress,
        AiProviders.OpenRouter => OpenRouterBaseAddress,
        _ => throw new ArgumentOutOfRangeException(
            nameof(provider),
            provider,
            "The AI provider is not supported."),
    };

    public static string GetEndpointProfile(string provider) => provider switch
    {
        AiProviders.GeminiDirect => GeminiEndpointProfile,
        AiProviders.OpenRouter => OpenRouterEndpointProfile,
        _ => throw new ArgumentOutOfRangeException(
            nameof(provider),
            provider,
            "The AI provider is not supported."),
    };

    public static bool IsModelIdValid(string provider, string? modelId) =>
        modelId is { Length: > 0 and <= 128 }
        && provider switch
        {
            AiProviders.GeminiDirect => GeminiModelIdPattern().IsMatch(modelId),
            AiProviders.OpenRouter => OpenRouterModelIdPattern().IsMatch(modelId)
                && !modelId.StartsWith(
                    "openrouter/",
                    StringComparison.OrdinalIgnoreCase)
                && !modelId.EndsWith(
                    ":online",
                    StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    public static bool SupportsImageTasks(string provider, string modelId) =>
        IsModelIdValid(provider, modelId)
        && !(provider == AiProviders.OpenRouter
            && IsDeepSeekV4FlashFamily(modelId));

    public static bool IsDeepSeekV4FlashFamily(string modelId) =>
        string.Equals(
            modelId,
            DeepSeekV4FlashModelId,
            StringComparison.OrdinalIgnoreCase)
        || modelId.StartsWith(
            DeepSeekV4FlashModelId + "-",
            StringComparison.OrdinalIgnoreCase)
        || modelId.StartsWith(
            DeepSeekV4FlashModelId + ":",
            StringComparison.OrdinalIgnoreCase);

    public static bool IsConnectionShapeValid(
        string provider,
        string endpointProfile,
        string modelId) =>
        IsSupportedProvider(provider)
        && endpointProfile == GetEndpointProfile(provider)
        && IsModelIdValid(provider, modelId);

    [GeneratedRegex(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex GeminiModelIdPattern();

    [GeneratedRegex(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}/[A-Za-z0-9][A-Za-z0-9._:@+-]{0,62}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex OpenRouterModelIdPattern();
}

public static class AiTaskTypes
{
    public const string TemplateExtraction = "templateExtraction";
    public const string NameTranscription = "nameTranscription";
    public const string InitialGrading = "initialGrading";
    public const string Adjudication = "adjudication";
}

public sealed record AiConnectionSettings(
    string ConnectionId,
    string Provider,
    Uri BaseAddress,
    string ModelId,
    TimeSpan Timeout);

public sealed record AiMediaPart(
    string MimeType,
    ReadOnlyMemory<byte> Bytes,
    string Sha256);

public sealed record AiProviderRequest(
    string RequestKey,
    string TaskType,
    string PromptVersion,
    string SchemaVersion,
    string SystemInstruction,
    string UserInstruction,
    JsonElement ResponseJsonSchema,
    IReadOnlyList<AiMediaPart> Media,
    int MaxOutputTokens = 8_192,
    string MediaResolution = "MEDIA_RESOLUTION_HIGH",
    string ThinkingLevel = "MINIMAL");

/// <summary>
/// Normalized provider usage. <see cref="OutputTokens"/> excludes
/// <see cref="ThinkingTokens"/> so a provider that reports reasoning as part
/// of its completion-token total cannot be charged for the same tokens twice.
/// </summary>
/// <param name="PromptTokens">All prompt tokens, including cached reads.</param>
/// <param name="CachedTokens">The prompt-token subset served from cache.</param>
/// <param name="OutputTokens">Generated output excluding thinking tokens.</param>
/// <param name="ThinkingTokens">Reasoning or thinking tokens.</param>
/// <param name="TotalTokens">
/// The provider-reported total across prompt, output, and thinking tokens.
/// </param>
/// <param name="ProviderCostUsdMicros">
/// Provider-reported authoritative request cost, rounded up to USD micros.
/// Null means the provider did not supply a usable cost and must never be
/// interpreted as zero.
/// </param>
public sealed record AiUsage(
    int? PromptTokens,
    int? CachedTokens,
    int? OutputTokens,
    int? ThinkingTokens,
    int? TotalTokens,
    long? ProviderCostUsdMicros = null);

/// <summary>Normalized response returned by the configured API provider.</summary>
/// <param name="RoutedProvider">
/// Optional upstream route selected by an aggregator such as OpenRouter. This
/// is distinct from <paramref name="Provider"/>, which remains the configured
/// adapter identity used for policy and metadata validation.
/// </param>
public sealed record AiProviderResponse(
    string Provider,
    string RequestedModel,
    string? ActualModel,
    string? ProviderResponseId,
    string FinishReason,
    JsonElement StructuredOutput,
    AiUsage Usage,
    TimeSpan Latency,
    string? RoutedProvider = null);

public sealed record AiCapabilityProbeResult(
    bool Authentication,
    bool ModelAvailable,
    bool ImageInput,
    bool StructuredOutput,
    bool UsageMetadata,
    string State,
    string? SafeErrorCode,
    TimeSpan? Latency);

public interface IAiProviderClient
{
    string Provider { get; }

    Task<AiProviderResponse> GenerateAsync(
        AiConnectionSettings connection,
        ReadOnlyMemory<byte> credentialUtf8,
        AiProviderRequest request,
        CancellationToken cancellationToken = default);

    Task<AiCapabilityProbeResult> ProbeAsync(
        AiConnectionSettings connection,
        ReadOnlyMemory<byte> credentialUtf8,
        CancellationToken cancellationToken = default);
}

public enum AiBatchRemoteState
{
    Unspecified,
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Expired,
}

public enum AiBatchCreateFailureKind
{
    DefinitePreSend,
    DefiniteRemoteRejection,
    AmbiguousAfterSend,
}

public sealed class AiBatchCreateException : Exception
{
    public AiBatchCreateException(
        AiBatchCreateFailureKind kind,
        string safeErrorCode,
        bool isTransient,
        TimeSpan? retryAfter = null,
        Exception? innerException = null)
        : base(safeErrorCode, innerException)
    {
        if (string.IsNullOrWhiteSpace(safeErrorCode)
            || safeErrorCode.Length > 200)
        {
            throw new ArgumentException(
                "A bounded safe error code is required.",
                nameof(safeErrorCode));
        }

        Kind = kind;
        SafeErrorCode = safeErrorCode;
        IsTransient = isTransient;
        RetryAfter = retryAfter;
    }

    public AiBatchCreateFailureKind Kind { get; }

    public string SafeErrorCode { get; }

    public bool IsTransient { get; }

    public TimeSpan? RetryAfter { get; }
}

public sealed record AiBatchInputFile(
    string ProviderFileName,
    string? ProviderFileUri,
    DateTimeOffset? ExpiresAt,
    long Bytes);

public sealed record AiBatchCreateRequest(
    string DisplayName,
    string ManifestHash,
    string InputFileName,
    int RequestCount);

public sealed record AiBatchCreateReceipt(
    string ProviderBatchName,
    string DisplayName,
    DateTimeOffset? CreatedAt);

public sealed record AiBatchStats(
    long RequestCount,
    long SuccessfulRequestCount,
    long FailedRequestCount,
    long PendingRequestCount);

public sealed record AiBatchStatus(
    string ProviderBatchName,
    string? DisplayName,
    AiBatchRemoteState State,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? EndedAt,
    AiBatchStats? Stats,
    string? OutputFileName,
    string? SafeErrorCode,
    JsonElement RawEnvelope);

public sealed record AiBatchListPage(
    IReadOnlyList<AiBatchStatus> Batches,
    string? NextPageToken);

public sealed record AiBatchItemResult(
    string RequestKey,
    AiProviderResponse? Response,
    string? SafeErrorCode);

/// <summary>
/// Provider-neutral asynchronous batch boundary. Batch creation is deliberately
/// separate from upload and polling because providers may not make creation
/// idempotent. Callers must never repeat an ambiguous create automatically.
/// </summary>
public interface IAiBatchProviderClient
{
    string Provider { get; }

    byte[] BuildJsonLines(IReadOnlyList<AiProviderRequest> requests);

    Task<AiBatchInputFile> UploadJsonLinesAsync(
        AiConnectionSettings connection,
        ReadOnlyMemory<byte> credentialUtf8,
        string displayName,
        ReadOnlyMemory<byte> jsonLines,
        CancellationToken cancellationToken = default);

    Task<AiBatchCreateReceipt> CreateAsync(
        AiConnectionSettings connection,
        ReadOnlyMemory<byte> credentialUtf8,
        AiBatchCreateRequest request,
        CancellationToken cancellationToken = default);

    Task<AiBatchStatus> GetAsync(
        AiConnectionSettings connection,
        ReadOnlyMemory<byte> credentialUtf8,
        string providerBatchName,
        CancellationToken cancellationToken = default);

    Task CancelAsync(
        AiConnectionSettings connection,
        ReadOnlyMemory<byte> credentialUtf8,
        string providerBatchName,
        CancellationToken cancellationToken = default);

    Task DeleteBatchAsync(
        AiConnectionSettings connection,
        ReadOnlyMemory<byte> credentialUtf8,
        string providerBatchName,
        CancellationToken cancellationToken = default);

    Task<AiBatchListPage> ListAsync(
        AiConnectionSettings connection,
        ReadOnlyMemory<byte> credentialUtf8,
        string? pageToken = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AiBatchItemResult>> ReadResultsAsync(
        AiConnectionSettings connection,
        ReadOnlyMemory<byte> credentialUtf8,
        AiBatchStatus completedBatch,
        CancellationToken cancellationToken = default);

    Task DeleteFileAsync(
        AiConnectionSettings connection,
        ReadOnlyMemory<byte> credentialUtf8,
        string providerFileName,
        CancellationToken cancellationToken = default);
}

public sealed record AiPromptBundle(
    string TaskType,
    string PromptVersion,
    string SchemaVersion,
    string SystemInstruction,
    JsonElement ResponseJsonSchema,
    string ContentHash);

public interface IAiPromptBundleCatalog
{
    AiPromptBundle GetRequired(string taskType);
}
