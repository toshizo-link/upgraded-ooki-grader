using System.Net;
using System.Text;
using System.Text.Json;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Ai.Gemini;
using OokiGrader.Host.Jobs;

namespace OokiGrader.IntegrationTests;

public sealed class GeminiTaskRoutingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(),
        "ooki-routing-test-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private readonly List<(string Model, string Body)> _sent = [];
    private static readonly byte[] Credential = Encoding.UTF8.GetBytes("synthetic-routing-key");
    private static readonly AiConnectionSettings Connection = new("connection", AiProviders.GeminiDirect,
        AiProviderCatalog.GeminiBaseAddress, AiProviderCatalog.GeminiDefaultModelId, TimeSpan.FromSeconds(10));

    [Theory]
    [InlineData(AiTaskTypes.TemplateExtraction)]
    [InlineData(AiTaskTypes.InitialGrading)]
    [InlineData(AiTaskTypes.Adjudication)]
    public async Task ImportantWorkKeepsAllPagesAndUsesPreferredModel(string task)
    {
        var response = await Client(_ => Success()).GenerateAsync(Connection, Credential, Request(task));
        Assert.Single(_sent);
        Assert.Equal(AiProviderCatalog.GeminiDefaultModelId, _sent[0].Model);
        Assert.Null(response.ModelRoutingReason);
        Assert.True(AiResponseMetadataValidator.IsAccepted(response));
        using var body = JsonDocument.Parse(_sent[0].Body);
        Assert.Equal(3, body.RootElement.GetProperty("contents")[0].GetProperty("parts")
            .EnumerateArray().Count(part => part.TryGetProperty("inline_data", out _)));
    }

    [Fact]
    public async Task IdentityUsesLiteWithoutSpendingPreferredModelQuota()
    {
        var response = await Client(_ => Success()).GenerateAsync(Connection, Credential,
            Request(AiTaskTypes.NameTranscription));
        Assert.Equal(AiProviderCatalog.GeminiLightModelId, Assert.Single(_sent).Model);
        Assert.Equal(GeminiTaskRoutingClient.LightTaskReason, response.ModelRoutingReason);
        Assert.Equal(AiProviderCatalog.GeminiDefaultModelId, response.RequestedModel);
        Assert.Equal(AiProviderCatalog.GeminiLightModelId, response.ActualModel);
        Assert.True(AiResponseMetadataValidator.IsAccepted(response));
    }

    [Fact]
    public async Task RateLimitRetriesIdenticalBatchedWorkOnceAndPersistsCooldownAcrossRestart()
    {
        var client = Client(model => model == AiProviderCatalog.GeminiDefaultModelId
            ? Quota("60s") : Success());
        var response = await client.GenerateAsync(Connection, Credential, Request(AiTaskTypes.InitialGrading));
        Assert.Equal(2, _sent.Count);
        Assert.Equal(_sent[0].Body, _sent[1].Body);
        Assert.Equal(AiProviderCatalog.GeminiLightModelId, response.ActualModel);
        Assert.Equal(GeminiTaskRoutingClient.QuotaReason, response.ModelRoutingReason);
        Assert.True(AiResponseMetadataValidator.IsAccepted(response));
        Assert.Equal(321, response.Usage.TotalTokens);
        var restarted = Client(_ => Success());
        await restarted.GenerateAsync(Connection, Credential, Request(AiTaskTypes.Adjudication));
        Assert.Equal(AiProviderCatalog.GeminiLightModelId, _sent[^1].Model);
        Assert.DoesNotContain("synthetic-routing-key", string.Join("", Directory.GetFiles(_directory)
            .Select(File.ReadAllText)));
        _clock.Now = _clock.Now.AddSeconds(61);
        await restarted.GenerateAsync(Connection, Credential, Request(AiTaskTypes.Adjudication));
        Assert.Equal(AiProviderCatalog.GeminiDefaultModelId, _sent[^1].Model);
    }

    [Fact]
    public async Task ConcurrentImportantJobsObserveOneQuotaRejectionThenUseLite()
    {
        var client = Client(model => model == AiProviderCatalog.GeminiDefaultModelId
            ? Quota("300s") : Success());
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.GenerateAsync(
            Connection, Credential, Request(AiTaskTypes.InitialGrading))));
        Assert.Equal(1, _sent.Count(item => item.Model == AiProviderCatalog.GeminiDefaultModelId));
        Assert.Equal(8, _sent.Count(item => item.Model == AiProviderCatalog.GeminiLightModelId));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task OtherFailuresDoNotSendTheWorkAgain(int status)
    {
        await Assert.ThrowsAsync<AiProviderException>(() => Client(_ => new HttpResponseMessage(
            (HttpStatusCode)status) { Content = new StringContent("{}") }).GenerateAsync(
                Connection, Credential, Request(AiTaskTypes.InitialGrading)));
        Assert.Single(_sent);
    }

    [Fact]
    public async Task LiteQuotaFailureSurfacesWithoutLooping()
    {
        var exception = await Assert.ThrowsAsync<AiProviderException>(() => Client(_ => Quota("60s"))
            .GenerateAsync(Connection, Credential, Request(AiTaskTypes.InitialGrading)));
        Assert.Equal(AiFailureKind.RateLimited, exception.Kind);
        Assert.Equal(2, _sent.Count);
    }

    [Fact]
    public async Task UnapprovedModelIsRejectedEvenWithRoutingMarker()
    {
        var response = await Client(_ => Success()).GenerateAsync(Connection, Credential,
            Request(AiTaskTypes.NameTranscription));
        Assert.False(AiResponseMetadataValidator.IsAccepted(response with { ModelRoutingReason = null }));
        Assert.False(AiResponseMetadataValidator.IsAccepted(response with { ActualModel = "gemini-unapproved" }));
        Assert.False(AiResponseMetadataValidator.IsAccepted(response with { RequestedModel = "different" }));
        Assert.False(AiResponseMetadataValidator.IsAccepted(response with { FinishReason = "MAX_TOKENS" }));
        Assert.False(AiResponseMetadataValidator.IsAccepted(response with { Provider = AiProviders.OpenRouter }));
    }

    [Fact]
    public async Task DailyQuotaUsesPacificResetInsteadOfShortRetryHint()
    {
        var direct = new GeminiDirectClient(new HttpClient(new Handler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(
                """{"error":{"details":[{"violations":[{"quotaId":"GenerateRequestsPerDayPerProjectPerModel-FreeTier"}]},{"retryDelay":"2s"}]}}""") }))));
        var before = DateTimeOffset.UtcNow;
        var exception = await Assert.ThrowsAsync<AiProviderException>(() => direct.GenerateAsync(
            Connection, Credential, Request(AiTaskTypes.InitialGrading)));
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        var expected = TimeZoneInfo.ConvertTimeToUtc(TimeZoneInfo.ConvertTime(before, zone).Date.AddDays(1), zone);
        Assert.InRange((before + exception.RetryAfter!.Value - expected).TotalSeconds, -5, 5);
    }

    private GeminiTaskRoutingClient Client(Func<string, HttpResponseMessage> answer) => new(
        new GeminiDirectClient(new HttpClient(new Handler(async message =>
        {
            var model = message.RequestUri!.AbsolutePath.Split("models/")[1].Split(':')[0];
            var body = await message.Content!.ReadAsStringAsync();
            lock (_sent) _sent.Add((model, body));
            return answer(model);
        })), (_, _) => Task.CompletedTask), new GeminiQuotaCooldownStore(_directory, _clock));

    [Fact]
    public async Task ServiceFailureRetriesThenUsesLiteWithoutTreatingItAsQuota()
    {
        var client = Client(model => model == AiProviderCatalog.GeminiDefaultModelId
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Success());
        var response = await client.GenerateAsync(Connection, Credential, Request(AiTaskTypes.InitialGrading));
        Assert.Equal(4, _sent.Count);
        Assert.All(_sent.Take(3), item => Assert.Equal(AiProviderCatalog.GeminiDefaultModelId, item.Model));
        Assert.Equal(AiProviderCatalog.GeminiLightModelId, _sent[3].Model);
        Assert.Equal(_sent[0].Body, _sent[3].Body);
        Assert.Equal(GeminiTaskRoutingClient.ServiceReason, response.ModelRoutingReason);
        Assert.True(AiResponseMetadataValidator.IsAccepted(response));
        Assert.False(Directory.Exists(_directory));
        await client.GenerateAsync(Connection, Credential, Request(AiTaskTypes.Adjudication));
        Assert.Equal(AiProviderCatalog.GeminiDefaultModelId, _sent[4].Model);
    }

    [Fact]
    public async Task BothModelsUnavailableFailBoundedlyWithoutAcceptingFabricatedResults()
    {
        var exception = await Assert.ThrowsAsync<AiProviderException>(() => Client(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)).GenerateAsync(Connection,
                Credential, Request(AiTaskTypes.InitialGrading)));
        Assert.Equal(503, exception.HttpStatusCode);
        Assert.Equal(6, _sent.Count);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task RepeatedServiceFailureCanProbeLiteButLiteMustActuallyPass()
    {
        var probe = await Client(model => model == AiProviderCatalog.GeminiDefaultModelId
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Success()).ProbeAsync(Connection, Credential);
        Assert.Equal("passed", probe.State);
        Assert.Equal(4, _sent.Count);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task QuotaFallbackPreservesTemplatePagesAndUsesHigherLiteReasoning()
    {
        await Client(model => model == AiProviderCatalog.GeminiDefaultModelId ? Quota("60s") : Success())
            .GenerateAsync(Connection, Credential, Request(AiTaskTypes.TemplateExtraction));
        using var before = JsonDocument.Parse(_sent[0].Body);
        using var after = JsonDocument.Parse(_sent[1].Body);
        Assert.Equal(before.RootElement.GetProperty("contents").GetRawText(),
            after.RootElement.GetProperty("contents").GetRawText());
        Assert.Equal(before.RootElement.GetProperty("generationConfig").GetProperty("responseJsonSchema").GetRawText(),
            after.RootElement.GetProperty("generationConfig").GetProperty("responseJsonSchema").GetRawText());
        Assert.Equal("HIGH", after.RootElement.GetProperty("generationConfig")
            .GetProperty("thinkingConfig").GetProperty("thinkingLevel").GetString());
    }

    [Fact]
    public async Task CapabilityProbeVerifiesLiteAndRemainsUsableDuringPrimaryQuotaCooldown()
    {
        var client = Client(model => model == AiProviderCatalog.GeminiDefaultModelId
            ? Quota("120s") : Success());
        Assert.Equal("passed", (await client.ProbeAsync(Connection, Credential)).State);
        Assert.Equal(2, _sent.Count);
        Assert.Equal("passed", (await Client(_ => Success()).ProbeAsync(Connection, Credential)).State);
        Assert.Equal(AiProviderCatalog.GeminiLightModelId, _sent[^1].Model);
        Assert.Equal(3, _sent.Count);
    }

    [Fact]
    public async Task FailedLiteProbeDoesNotMarkConnectionReady()
    {
        var result = await Client(model => model == AiProviderCatalog.GeminiDefaultModelId
            ? Success() : new HttpResponseMessage(HttpStatusCode.Forbidden))
            .ProbeAsync(Connection, Credential);
        Assert.Equal("failed", result.State);
        Assert.False(result.Authentication);
    }

    [Fact]
    public async Task CooldownFollowsTheCredentialAcrossConnectionsButDoesNotThrottleNewCredentials()
    {
        await Client(model => model == AiProviderCatalog.GeminiDefaultModelId
            ? Quota("300s") : Success()).GenerateAsync(Connection, Credential,
                Request(AiTaskTypes.InitialGrading));
        var client = Client(_ => Success());
        await client.GenerateAsync(Connection with { ConnectionId = "another-connection" },
            Credential, Request(AiTaskTypes.InitialGrading));
        Assert.Equal(AiProviderCatalog.GeminiLightModelId, _sent[^1].Model);
        await client.GenerateAsync(Connection, Encoding.UTF8.GetBytes("another-synthetic-key"),
            Request(AiTaskTypes.InitialGrading));
        Assert.Equal(AiProviderCatalog.GeminiDefaultModelId, _sent[^1].Model);
    }

    [Fact]
    public async Task ExplicitAlternativeModelDoesNotReceiveDefaultRoutingPolicy()
    {
        var response = await Client(_ => Success()).GenerateAsync(
            Connection with { ModelId = "gemini-other-approved-model" }, Credential,
            Request(AiTaskTypes.NameTranscription));
        Assert.Equal("gemini-other-approved-model", Assert.Single(_sent).Model);
        Assert.Null(response.ModelRoutingReason);
    }

    private static AiProviderRequest Request(string task)
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"]}""");
        return new("same-request", task, "v1", "v1", "system", "all questions for one student",
            schema.RootElement.Clone(), Enumerable.Range(1, 3).Select(index =>
            {
                var bytes = Encoding.UTF8.GetBytes("page " + index);
                return new AiMediaPart("image/png", bytes, Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
            }).ToArray(), ThinkingLevel: "MEDIUM");
    }

    private static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    { Content = new StringContent("""{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"{\"ok\":true}"}]}}],"usageMetadata":{"promptTokenCount":300,"candidatesTokenCount":21,"totalTokenCount":321}}""") };

    private static HttpResponseMessage Quota(string delay) => new(HttpStatusCode.TooManyRequests)
    { Content = new StringContent(JsonSerializer.Serialize(new { error = new { details = new[] { new { retryDelay = delay } } } })) };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
