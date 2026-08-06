using OokiGrader.Ai.Abstractions;
using OokiGrader.Host.Jobs;

namespace OokiGrader.IntegrationTests;

public sealed class AiProviderClientResolverTests
{
    [Fact]
    public void ResolvesTheExactRegisteredGeminiAndOpenRouterClients()
    {
        var gemini = new StubProviderClient(AiProviders.GeminiDirect);
        var openRouter = new StubProviderClient(AiProviders.OpenRouter);
        var resolver = new AiProviderClientResolver([gemini, openRouter]);

        Assert.Same(gemini, resolver.GetRequired(AiProviders.GeminiDirect));
        Assert.Same(openRouter, resolver.GetRequired(AiProviders.OpenRouter));
    }

    [Fact]
    public void RejectsUnknownProvidersWithASafeConfigurationError()
    {
        var resolver = new AiProviderClientResolver(
            [new StubProviderClient(AiProviders.GeminiDirect)]);

        var exception = Assert.Throws<AiProviderException>(
            () => resolver.GetRequired("unconfigured-provider"));

        Assert.Equal(AiFailureKind.InvalidConfiguration, exception.Kind);
        Assert.Equal("ai_provider_not_configured", exception.SafeErrorCode);
        Assert.False(exception.IsTransient);
    }

    private sealed class StubProviderClient(string provider)
        : IAiProviderClient
    {
        public string Provider { get; } = provider;

        public Task<AiProviderResponse> GenerateAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            AiProviderRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AiCapabilityProbeResult> ProbeAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
