using OokiGrader.Ai.Abstractions;

namespace OokiGrader.Host.Jobs;

public interface IAiProviderClientResolver
{
    IAiProviderClient GetRequired(string provider);
}

public interface IAiProviderFeaturePolicy
{
    bool IsEnabled(string provider);
}

public sealed class AiProviderFeaturePolicy(
    bool geminiDirectEnabled,
    bool openRouterEnabled) : IAiProviderFeaturePolicy
{
    public static AiProviderFeaturePolicy AllowAll { get; } = new(true, true);

    public bool IsEnabled(string provider) => provider switch
    {
        AiProviders.GeminiDirect => geminiDirectEnabled,
        AiProviders.OpenRouter => openRouterEnabled,
        _ => false,
    };
}

public sealed class AiProviderClientResolver : IAiProviderClientResolver
{
    private readonly Dictionary<string, IAiProviderClient> _clients;

    public AiProviderClientResolver(IEnumerable<IAiProviderClient> clients)
    {
        ArgumentNullException.ThrowIfNull(clients);
        var configured = new Dictionary<string, IAiProviderClient>(
            StringComparer.Ordinal);
        foreach (var client in clients)
        {
            ArgumentNullException.ThrowIfNull(client);
            if (!configured.TryAdd(client.Provider, client))
            {
                throw new InvalidOperationException(
                    $"More than one AI provider client is registered for '{client.Provider}'.");
            }
        }

        _clients = configured;
    }

    public IAiProviderClient GetRequired(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider)
            || !_clients.TryGetValue(provider, out var client))
        {
            throw new AiProviderException(
                AiFailureKind.InvalidConfiguration,
                "ai_provider_not_configured",
                isTransient: false);
        }

        return client;
    }
}

internal static class AiProviderRuntime
{
    public const string GeminiModel = "gemini-3.5-flash-lite";
    public const string OpenRouterDefaultModel =
        "google/gemini-3.1-flash-lite";

    public static string NormalizeProvider(string? provider) => provider switch
    {
        null or "" or AiProviders.GeminiDirect or "gemini_direct" =>
            AiProviders.GeminiDirect,
        AiProviders.OpenRouter or "openrouter" or "open_router" =>
            AiProviders.OpenRouter,
        _ => string.Empty,
    };

    public static string DefaultModel(string provider) => provider switch
    {
        AiProviders.GeminiDirect => GeminiModel,
        AiProviders.OpenRouter => OpenRouterDefaultModel,
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    public static string DisplayName(string provider) => provider switch
    {
        AiProviders.GeminiDirect => "Gemini",
        AiProviders.OpenRouter => "OpenRouter",
        _ => provider,
    };

    public static bool IsAmbiguousDispatch(AiProviderException exception) =>
        exception.Kind is AiFailureKind.Timeout
        || exception.SafeErrorCode.EndsWith(
            "_network_error",
            StringComparison.Ordinal);

    public static bool ShouldRetry(AiProviderException exception) =>
        exception.Kind is AiFailureKind.RateLimited
        || (exception.Kind is AiFailureKind.TransientProvider
            && exception.IsTransient);

    public static long ResolveActualUsdMicros(
        AiUsage usage,
        long reservedUsdMicros,
        Func<long>? tokenCostFactory)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (usage.ProviderCostUsdMicros is { } providerCost)
        {
            return providerCost;
        }

        // Missing usage is not evidence of a free request. Preserve the
        // conservative reservation unless enough billable token data exists.
        return tokenCostFactory is not null
            && usage.PromptTokens is not null
            && usage.OutputTokens is not null
                ? tokenCostFactory()
                : reservedUsdMicros;
    }
}
