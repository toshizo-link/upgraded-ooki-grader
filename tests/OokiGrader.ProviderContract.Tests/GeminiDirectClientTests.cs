using System.Net;
using System.Text;
using System.Text.Json;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Ai.Gemini;

namespace OokiGrader.ProviderContract.Tests;

public sealed class GeminiDirectClientTests
{
    [Fact]
    public void ApprovedTemplateExtractionBundleIsVersionedAndSourceAware()
    {
        using var catalog = new ApprovedPromptBundleCatalog();
        var bundle = catalog.GetRequired(AiTaskTypes.TemplateExtraction);

        Assert.Equal("template-extract-v2.0.0", bundle.PromptVersion);
        Assert.Equal("template_extract_v5", bundle.SchemaVersion);
        Assert.Contains(
            "If any page needs a non-zero\nturn, return action=rotate",
            bundle.SystemInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "Do not return questions, answers, names, grades",
            bundle.SystemInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "use your own subject-matter knowledge only",
            bundle.SystemInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "marked ai_proposed and have no answer source",
            bundle.SystemInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "Never use visible filled responses",
            bundle.SystemInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "preserve Japanese script exactly",
            bundle.SystemInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "visible みず must remain みず, never 水",
            bundle.SystemInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "never splice a kana proposal and a Kanji proposal",
            bundle.SystemInstruction,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Do not use web search or external knowledge.",
            bundle.SystemInstruction,
            StringComparison.Ordinal);
        var metadataProperties = bundle.ResponseJsonSchema
            .GetProperty("properties")
            .GetProperty("metadata")
            .GetProperty("anyOf");
        Assert.Contains(metadataProperties.EnumerateArray(), item =>
            item.TryGetProperty("$ref", out var reference)
            && reference.GetString() == "#/$defs/metadata");
        var rootProperties = bundle.ResponseJsonSchema.GetProperty("properties");
        Assert.True(rootProperties.TryGetProperty("action", out _));
        Assert.True(rootProperties.TryGetProperty("orientation", out _));
        var extractedMetadata = bundle.ResponseJsonSchema
            .GetProperty("$defs")
            .GetProperty("metadata")
            .GetProperty("properties");
        Assert.True(extractedMetadata.TryGetProperty("printed_test_name", out _));
        Assert.True(extractedMetadata.TryGetProperty("printed_grade_label", out _));
        Assert.False(extractedMetadata.TryGetProperty("category", out _));
        Assert.False(extractedMetadata.TryGetProperty("subject", out _));
        var pageProperties = bundle.ResponseJsonSchema
            .GetProperty("properties")
            .GetProperty("pages")
            .GetProperty("items")
            .GetProperty("properties");
        Assert.True(pageProperties.TryGetProperty("source_id", out _));
        Assert.True(
            pageProperties.TryGetProperty(
                "detected_answer_slot_count",
                out _));
        Assert.False(
            pageProperties.TryGetProperty(
                "student_number_region",
                out _));
        var questionProperties = pageProperties
            .GetProperty("questions")
            .GetProperty("items")
            .GetProperty("properties");
        Assert.False(
            questionProperties.TryGetProperty("question_region", out _));
        Assert.False(
            questionProperties.TryGetProperty("answer_region", out _));
        Assert.True(
            questionProperties.TryGetProperty("answer_source", out _));
        Assert.True(
            questionProperties.TryGetProperty("accepted_variants", out _));
        Assert.True(
            questionProperties.TryGetProperty("answer_slot_ordinal", out _));
        Assert.True(
            questionProperties.TryGetProperty("answer_slot_count", out _));
        Assert.True(
            questionProperties.TryGetProperty("filled_answer_removed", out _));
        Assert.True(
            questionProperties.TryGetProperty("is_embedded_fill_blank", out _));
    }

    [Fact]
    public void ApprovedGradingBundlesDefineCombinedIdentityAndGradingContract()
    {
        using var catalog = new ApprovedPromptBundleCatalog();
        var initial = catalog.GetRequired(AiTaskTypes.InitialGrading);
        var adjudication = catalog.GetRequired(AiTaskTypes.Adjudication);

        Assert.Equal("submission-analyze-v2.1.0", initial.PromptVersion);
        Assert.Equal("answer-recheck-v1.3.0", adjudication.PromptVersion);
        Assert.Equal("submission_analysis_v2", initial.SchemaVersion);
        Assert.Equal("answer_transcribe_grade_v1", adjudication.SchemaVersion);

        foreach (var bundle in new[] { initial, adjudication })
        {
            Assert.Contains(
                "Never put a located blank answer in missing_question_ids",
                bundle.SystemInstruction,
                StringComparison.Ordinal);
            Assert.Contains(
                "exact integer\nmultiple of that question's point_increment_milli",
                bundle.SystemInstruction,
                StringComparison.Ordinal);
            Assert.Contains(
                "directly from the original supplied\npage pixels in one integrated inspection",
                bundle.SystemInstruction,
                StringComparison.Ordinal);
            Assert.Contains(
                "must\nnever be the sole input to the grading decision",
                bundle.SystemInstruction,
                StringComparison.Ordinal);

            var responseProperties = bundle.ResponseJsonSchema
                .GetProperty("properties");
            var resultProperties = responseProperties
                .GetProperty("results")
                .GetProperty("items")
                .GetProperty("properties");
            Assert.Contains(
                "A blank answer is a result, never a missing question",
                resultProperties
                    .GetProperty("blank")
                    .GetProperty("description")
                    .GetString() ?? string.Empty,
                StringComparison.Ordinal);
            Assert.Contains(
                "exact multiple of the supplied point_increment_milli",
                resultProperties
                    .GetProperty("proposed_points_milli")
                    .GetProperty("description")
                    .GetString() ?? string.Empty,
                StringComparison.Ordinal);
            Assert.Contains(
                "line boundary preserved as \\n",
                resultProperties
                    .GetProperty("transcription")
                    .GetProperty("description")
                    .GetString() ?? string.Empty,
                StringComparison.Ordinal);
            Assert.Contains(
                "Do not include located blank, unreadable, cropped, or ambiguous answers",
                responseProperties
                    .GetProperty("missing_question_ids")
                    .GetProperty("description")
                    .GetString() ?? string.Empty,
                StringComparison.Ordinal);
        }

        Assert.Contains(
            "Only when identity_required=true",
            initial.SystemInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "The host matches against its roster locally",
            initial.SystemInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "evidence_media_index",
            initial.SystemInstruction,
            StringComparison.Ordinal);
        var initialProperties = initial.ResponseJsonSchema
            .GetProperty("properties");
        Assert.True(initialProperties.TryGetProperty("identity", out var identity));
        Assert.Contains(identity.GetProperty("anyOf").EnumerateArray(), item =>
            item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty("type", out var type)
            && type.GetString() == "null");
        var initialResultProperties = initialProperties
            .GetProperty("results")
            .GetProperty("items")
            .GetProperty("properties");
        Assert.True(initialResultProperties.TryGetProperty(
            "evidence_media_index",
            out _));
        Assert.False(adjudication.ResponseJsonSchema
            .GetProperty("properties")
            .TryGetProperty("identity", out _));

        var nameTranscription = catalog.GetRequired(AiTaskTypes.NameTranscription);
        Assert.DoesNotContain(
            "missing_question_ids",
            nameTranscription.SystemInstruction,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsyncSendsStrictStructuredMultimodalRequest()
    {
        string? body = null;
        string? apiKey = null;
        var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            body = await request.Content!.ReadAsStringAsync(cancellationToken);
            apiKey = request.Headers.GetValues("x-goog-api-key").Single();
            return JsonResponse(
                """
                {
                  "candidates": [{
                    "content": {"parts": [{"text": "{\"ok\":true}"}]},
                    "finishReason": "STOP"
                  }],
                  "usageMetadata": {
                    "promptTokenCount": 11,
                    "candidatesTokenCount": 3,
                    "thoughtsTokenCount": 1,
                    "totalTokenCount": 15
                  },
                  "modelVersion": "gemini-3.5-flash-lite-001",
                  "responseId": "response-1"
                }
                """);
        });
        var client = new GeminiDirectClient(new HttpClient(handler));
        using var schema = JsonDocument.Parse(
            """{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"]}""");
        var media = Encoding.UTF8.GetBytes("synthetic-image");

        var response = await client.GenerateAsync(
            Connection(),
            Encoding.UTF8.GetBytes("test-key"),
            new AiProviderRequest(
                "request-1",
                AiTaskTypes.InitialGrading,
                "prompt-v1",
                "schema-v1",
                "system",
                "user",
                schema.RootElement.Clone(),
                [
                    new AiMediaPart(
                        "image/png",
                        media,
                        Convert.ToHexString(
                                System.Security.Cryptography.SHA256.HashData(media))
                            .ToLowerInvariant()),
                ]));

        Assert.Equal("test-key", apiKey);
        Assert.True(response.StructuredOutput.GetProperty("ok").GetBoolean());
        Assert.Equal(15, response.Usage.TotalTokens);
        Assert.Equal("gemini-3.5-flash-lite-001", response.ActualModel);
        Assert.NotNull(body);
        using var requestJson = JsonDocument.Parse(body);
        var configuration = requestJson.RootElement.GetProperty("generationConfig");
        Assert.Equal(
            "application/json",
            configuration.GetProperty("responseMimeType").GetString());
        Assert.Equal(
            "MINIMAL",
            configuration
                .GetProperty("thinkingConfig")
                .GetProperty("thinkingLevel")
                .GetString());
        Assert.False(configuration.TryGetProperty("temperature", out _));
        Assert.False(configuration.TryGetProperty("candidateCount", out _));
        Assert.Equal(
            JsonValueKind.Object,
            configuration.GetProperty("responseJsonSchema").ValueKind);
        Assert.Equal(
            JsonValueKind.String,
            requestJson.RootElement
                .GetProperty("contents")[0]
                .GetProperty("parts")[0]
                .GetProperty("inline_data")
                .GetProperty("data")
                .ValueKind);
    }

    [Fact]
    public async Task GenerateAsyncHonorsSupportedThinkingLevelOverride()
    {
        string? body = null;
        var client = new GeminiDirectClient(
            new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
            {
                body = await request.Content!.ReadAsStringAsync(cancellationToken);
                return JsonResponse(
                    """
                    {
                      "candidates": [{
                        "content": {"parts": [{"text": "{\"ok\":true}"}]},
                        "finishReason": "STOP"
                      }]
                    }
                    """);
            })));
        using var schema = JsonDocument.Parse(
            """{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"]}""");
        var media = new byte[] { 1 };

        await client.GenerateAsync(
            Connection() with { ModelId = "gemini-3.1-pro-preview" },
            Encoding.UTF8.GetBytes("test-key"),
            new AiProviderRequest(
                "request-1",
                AiTaskTypes.InitialGrading,
                "prompt-v1",
                "schema-v1",
                "system",
                "user",
                schema.RootElement.Clone(),
                [
                    new AiMediaPart(
                        "image/png",
                        media,
                        Convert.ToHexString(
                                System.Security.Cryptography.SHA256.HashData(media))
                            .ToLowerInvariant()),
                ],
                ThinkingLevel: "LOW"));

        Assert.NotNull(body);
        using var requestJson = JsonDocument.Parse(body);
        Assert.Equal(
            "LOW",
            requestJson.RootElement
                .GetProperty("generationConfig")
                .GetProperty("thinkingConfig")
                .GetProperty("thinkingLevel")
                .GetString());
    }

    [Fact]
    public async Task GenerateAsyncRejectsUnknownThinkingLevelBeforeSending()
    {
        var calls = 0;
        var client = new GeminiDirectClient(
            new HttpClient(new DelegateHandler((_, _) =>
            {
                calls++;
                return Task.FromResult(JsonResponse("{}"));
            })));
        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        var media = new byte[] { 1 };

        await Assert.ThrowsAsync<AiProviderException>(() =>
            client.GenerateAsync(
                Connection(),
                Encoding.UTF8.GetBytes("test-key"),
                new AiProviderRequest(
                    "request-1",
                    AiTaskTypes.InitialGrading,
                    "prompt-v1",
                    "schema-v1",
                    "system",
                    "user",
                    schema.RootElement.Clone(),
                    [
                        new AiMediaPart(
                            "image/png",
                            media,
                            Convert.ToHexString(
                                    System.Security.Cryptography.SHA256.HashData(media))
                                .ToLowerInvariant()),
                    ],
                    ThinkingLevel: "EXTREME")));

        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, AiFailureKind.Authentication, false)]
    [InlineData(HttpStatusCode.TooManyRequests, AiFailureKind.RateLimited, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AiFailureKind.TransientProvider, true)]
    public async Task GenerateAsyncClassifiesProviderFailures(
        HttpStatusCode status,
        AiFailureKind expectedKind,
        bool expectedTransient)
    {
        var client = new GeminiDirectClient(
            new HttpClient(new DelegateHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(status)))));
        using var schema = JsonDocument.Parse(
            """{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"]}""");
        var media = new byte[] { 1 };

        var exception = await Assert.ThrowsAsync<AiProviderException>(() =>
            client.GenerateAsync(
                Connection(),
                Encoding.UTF8.GetBytes("test-key"),
                new AiProviderRequest(
                    "request-1",
                    AiTaskTypes.InitialGrading,
                    "prompt-v1",
                    "schema-v1",
                    "system",
                    "user",
                    schema.RootElement.Clone(),
                    [
                        new AiMediaPart(
                            "image/png",
                            media,
                            Convert.ToHexString(
                                    System.Security.Cryptography.SHA256.HashData(media))
                                .ToLowerInvariant()),
                    ])));

        Assert.Equal(expectedKind, exception.Kind);
        Assert.Equal(expectedTransient, exception.IsTransient);
    }

    [Theory]
    [InlineData(
        "generation_config.response_json_schema.properties[pages]",
        "gemini_response_schema_invalid")]
    [InlineData(
        "generation_config.media_resolution",
        "gemini_media_resolution_invalid")]
    [InlineData(
        "generation_config.thinking_config.thinking_level",
        "gemini_thinking_config_invalid")]
    [InlineData(
        "generation_config.max_output_tokens",
        "gemini_output_limit_invalid")]
    [InlineData(
        "contents[0].parts[0].inline_data.mime_type",
        "gemini_media_invalid")]
    public async Task GenerateAsyncClassifiesBadRequestFieldWithoutExposingPayload(
        string field,
        string expectedSafeErrorCode)
    {
        const string secretProviderDetail = "provider-detail-must-not-escape";
        var providerError = JsonSerializer.Serialize(
            new
            {
                error = new
                {
                    code = 400,
                    message = secretProviderDetail,
                    status = "INVALID_ARGUMENT",
                    details = new object[]
                    {
                        new
                        {
                            fieldViolations = new[]
                            {
                                new
                                {
                                    field,
                                    description = secretProviderDetail,
                                },
                            },
                        },
                    },
                },
            });
        var client = new GeminiDirectClient(
            new HttpClient(new DelegateHandler((_, _) =>
                Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent(
                            providerError,
                            Encoding.UTF8,
                            "application/json"),
                    }))));
        using var schema = JsonDocument.Parse(
            """{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"]}""");
        var media = new byte[] { 1 };

        var exception = await Assert.ThrowsAsync<AiProviderException>(() =>
            client.GenerateAsync(
                Connection(),
                Encoding.UTF8.GetBytes("test-key"),
                new AiProviderRequest(
                    "request-1",
                    AiTaskTypes.InitialGrading,
                    "prompt-v1",
                    "schema-v1",
                    "system",
                    "user",
                    schema.RootElement.Clone(),
                    [
                        new AiMediaPart(
                            "image/png",
                            media,
                            Convert.ToHexString(
                                    System.Security.Cryptography.SHA256.HashData(media))
                                .ToLowerInvariant()),
                    ])));

        Assert.Equal(AiFailureKind.RequestRejected, exception.Kind);
        Assert.False(exception.IsTransient);
        Assert.Equal(expectedSafeErrorCode, exception.SafeErrorCode);
        Assert.DoesNotContain(
            secretProviderDetail,
            exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsyncClassifiesSchemaErrorFromProviderMessage()
    {
        var providerError = JsonSerializer.Serialize(
            new
            {
                error = new
                {
                    code = 400,
                    message =
                        "Invalid responseJsonSchema: schema complexity exceeds the supported limit.",
                    status = "INVALID_ARGUMENT",
                },
            });
        var client = new GeminiDirectClient(
            new HttpClient(new DelegateHandler((_, _) =>
                Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent(
                            providerError,
                            Encoding.UTF8,
                            "application/json"),
                    }))));
        using var schema = JsonDocument.Parse(
            """{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"]}""");
        var media = new byte[] { 1 };

        var exception = await Assert.ThrowsAsync<AiProviderException>(() =>
            client.GenerateAsync(
                Connection(),
                Encoding.UTF8.GetBytes("test-key"),
                new AiProviderRequest(
                    "request-1",
                    AiTaskTypes.InitialGrading,
                    "prompt-v1",
                    "schema-v1",
                    "system",
                    "user",
                    schema.RootElement.Clone(),
                    [
                        new AiMediaPart(
                            "image/png",
                            media,
                            Convert.ToHexString(
                                    System.Security.Cryptography.SHA256.HashData(media))
                                .ToLowerInvariant()),
                    ])));

        Assert.Equal(
            "gemini_response_schema_invalid",
            exception.SafeErrorCode);
        Assert.Equal(
            exception.SafeErrorCode,
            exception.Message);
    }

    [Theory]
    [InlineData("""{"error":{"status":"INVALID_ARGUMENT","message":"not classified"}}""")]
    [InlineData("""{"notError":"invalid"}""")]
    [InlineData("not-json")]
    public async Task GenerateAsyncKeepsGenericCodeForUnclassifiedBadRequest(
        string providerError)
    {
        var client = new GeminiDirectClient(
            new HttpClient(new DelegateHandler((_, _) =>
                Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent(
                            providerError,
                            Encoding.UTF8,
                            "application/json"),
                    }))));
        using var schema = JsonDocument.Parse(
            """{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"]}""");
        var media = new byte[] { 1 };

        var exception = await Assert.ThrowsAsync<AiProviderException>(() =>
            client.GenerateAsync(
                Connection(),
                Encoding.UTF8.GetBytes("test-key"),
                new AiProviderRequest(
                    "request-1",
                    AiTaskTypes.InitialGrading,
                    "prompt-v1",
                    "schema-v1",
                    "system",
                    "user",
                    schema.RootElement.Clone(),
                    [
                        new AiMediaPart(
                            "image/png",
                            media,
                            Convert.ToHexString(
                                    System.Security.Cryptography.SHA256.HashData(media))
                                .ToLowerInvariant()),
                    ])));

        Assert.Equal("gemini_request_invalid", exception.SafeErrorCode);
    }

    [Fact]
    public async Task GenerateAsyncRejectsNonGoogleEndpointBeforeSending()
    {
        var calls = 0;
        var client = new GeminiDirectClient(
            new HttpClient(new DelegateHandler((_, _) =>
            {
                calls++;
                return Task.FromResult(JsonResponse("{}"));
            })));
        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        var media = new byte[] { 1 };

        await Assert.ThrowsAsync<AiProviderException>(() =>
            client.GenerateAsync(
                Connection() with { BaseAddress = new Uri("https://example.test/") },
                Encoding.UTF8.GetBytes("test-key"),
                new AiProviderRequest(
                    "request-1",
                    AiTaskTypes.InitialGrading,
                    "prompt-v1",
                    "schema-v1",
                    "system",
                    "user",
                    schema.RootElement.Clone(),
                    [
                        new AiMediaPart(
                            "image/png",
                            media,
                            Convert.ToHexString(
                                    System.Security.Cryptography.SHA256.HashData(media))
                                .ToLowerInvariant()),
                    ])));

        Assert.Equal(0, calls);
    }

    private static AiConnectionSettings Connection() =>
        new(
            "connection-1",
            AiProviders.GeminiDirect,
            new Uri("https://generativelanguage.googleapis.com/"),
            "gemini-3.5-flash-lite",
            TimeSpan.FromSeconds(30));

    private static HttpResponseMessage JsonResponse(string value) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
