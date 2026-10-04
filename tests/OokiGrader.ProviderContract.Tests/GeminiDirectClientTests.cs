using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Ai.Gemini;

namespace OokiGrader.ProviderContract.Tests;

public sealed class GeminiDirectClientTests
{
    [Fact]
    public async Task Explicit503IsRetriedWithIdenticalPayloadThenReturnsOnlySuccessfulUsage()
    {
        var bodies = new List<string>();
        var delays = new List<TimeSpan>();
        var client = new GeminiDirectClient(new HttpClient(new DelegateHandler(async (message, token) =>
        {
            bodies.Add(await message.Content!.ReadAsStringAsync(token));
            return bodies.Count < 3 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : JsonResponse("""{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"{\"ok\":true}"}]}}],"usageMetadata":{"totalTokenCount":321}}""");
        })), (delay, _) => { delays.Add(delay); return Task.CompletedTask; });
        var response = await client.GenerateAsync(Connection(), Encoding.UTF8.GetBytes("test-key"), RetryRequest());
        Assert.Equal(3, bodies.Count);
        Assert.All(bodies, body => Assert.Equal(bodies[0], body));
        Assert.Equal(2, delays.Count);
        Assert.InRange(delays[0].TotalSeconds, 1, 1.25);
        Assert.InRange(delays[1].TotalSeconds, 2, 2.25);
        Assert.Equal(321, response.Usage.TotalTokens);
    }

    [Theory]
    [InlineData(503, 3)]
    [InlineData(429, 1)]
    [InlineData(403, 1)]
    [InlineData(400, 1)]
    public async Task RetryCountIsBoundedAndPermanentFailuresAreNotResent(int status, int expectedCalls)
    {
        var calls = 0;
        var client = new GeminiDirectClient(new HttpClient(new DelegateHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));
        })), (_, _) => Task.CompletedTask);
        var exception = await Assert.ThrowsAsync<AiProviderException>(() => client.GenerateAsync(
            Connection(), Encoding.UTF8.GetBytes("test-key"), RetryRequest()));
        Assert.Equal(expectedCalls, calls);
        if (status == 503) Assert.Equal(503, exception.HttpStatusCode);
    }

    [Fact]
    public async Task CancellationDuringRetryDelayPreventsAnotherSend()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var client = new GeminiDirectClient(new HttpClient(new DelegateHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        })), (_, token) => { cancellation.Cancel(); return Task.FromCanceled(token); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GenerateAsync(
            Connection(), Encoding.UTF8.GetBytes("test-key"), RetryRequest(), cancellation.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NetworkOutcomeUnknownIsNeverResent()
    {
        var calls = 0;
        var client = new GeminiDirectClient(new HttpClient(new DelegateHandler((_, _) =>
        {
            calls++;
            throw new HttpRequestException("synthetic network interruption");
        })), (_, _) => Task.CompletedTask);
        var exception = await Assert.ThrowsAsync<AiProviderException>(() => client.GenerateAsync(
            Connection(), Encoding.UTF8.GetBytes("test-key"), RetryRequest()));
        Assert.Equal("gemini_network_error", exception.SafeErrorCode);
        Assert.Equal(1, calls);
    }

    private static AiProviderRequest RetryRequest()
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"]}""");
        var bytes = new byte[] { 1, 2, 3 };
        return new("same-retry-key", AiTaskTypes.TemplateExtraction, "v1", "v1", "system", "all supplied pages",
            schema.RootElement.Clone(), [new AiMediaPart("image/png", bytes,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant())],
            ThinkingLevel: "MEDIUM");
    }

    [Theory]
    [InlineData("gemini-3.8-flash", "MINIMAL", "LOW")]
    [InlineData("gemini-3.8-flash-001", "MINIMAL", "LOW")]
    [InlineData("gemini-3.8-flash", "MEDIUM", "MEDIUM")]
    [InlineData("gemini-3.8-flash", "HIGH", "HIGH")]
    [InlineData("gemini-3.5-flash-lite", "MINIMAL", "MINIMAL")]
    public async Task GenerateAsyncNormalizesUnsupportedThinkingWithoutChangingValidOverrides(
        string modelId,
        string requestedThinking,
        string expectedThinking)
    {
        string? body = null;
        var client = new GeminiDirectClient(new HttpClient(
            new DelegateHandler(async (request, cancellationToken) =>
            {
                body = await request.Content!.ReadAsStringAsync(cancellationToken);
                return JsonResponse(
                    """{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"{\"ok\":true}"}]}}]}""");
            })));
        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        var media = new byte[] { 1 };
        await client.GenerateAsync(
            Connection() with { ModelId = modelId },
            Encoding.UTF8.GetBytes("test-key"),
            new AiProviderRequest(
                "thinking-regression", AiTaskTypes.TemplateExtraction, "v1", "v1",
                "system", "user", schema.RootElement.Clone(),
                [new AiMediaPart("application/pdf", media,
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(media)).ToLowerInvariant())],
                ThinkingLevel: requestedThinking));
        using var json = JsonDocument.Parse(body!);
        Assert.Equal(expectedThinking, json.RootElement.GetProperty("generationConfig")
            .GetProperty("thinkingConfig").GetProperty("thinkingLevel").GetString());
    }

    [Fact]
    public async Task GenerateAsyncRejectsTokenTruncatedOutputWithSpecificSafeCode()
    {
        var client = new GeminiDirectClient(new HttpClient(
            new DelegateHandler((_, _) => Task.FromResult(JsonResponse(
                """{"candidates":[{"finishReason":"MAX_TOKENS","content":{"parts":[{"text":"{\"incomplete\":"}]}}]}""")))));
        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        var media = new byte[] { 1 };
        var exception = await Assert.ThrowsAsync<AiProviderException>(() => client.GenerateAsync(
            Connection(), Encoding.UTF8.GetBytes("test-key"),
            new AiProviderRequest("truncated", AiTaskTypes.TemplateExtraction, "v1", "v1",
                "system", "user", schema.RootElement.Clone(),
                [new AiMediaPart("application/pdf", media,
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(media)).ToLowerInvariant())])));
        Assert.Equal(AiFailureKind.InvalidResponse, exception.Kind);
        Assert.Equal("gemini_output_limit_exceeded", exception.SafeErrorCode);
        Assert.False(exception.IsTransient);
    }

    [Theory]
    [InlineData("gemini-3.7-flash", "LOW")]
    [InlineData("gemini-3.7-flash-preview", "LOW")]
    [InlineData("gemini-3.8-flash", "LOW")]
    [InlineData("gemini-3.8-flash-preview", "LOW")]
    [InlineData("gemini-3.8-flash-001", "LOW")]
    [InlineData("GEMINI-3.8-FLASH", "LOW")]
    [InlineData("gemini-3.5-flash-lite", "MINIMAL")]
    public async Task ProbeAsyncUsesThinkingSupportedBySelectedModel(
        string modelId,
        string expectedThinking)
    {
        string? body = null;
        var client = new GeminiDirectClient(new HttpClient(
            new DelegateHandler(async (request, cancellationToken) =>
            {
                body = await request.Content!.ReadAsStringAsync(cancellationToken);
                Assert.EndsWith(
                    $"/models/{modelId}:generateContent",
                    request.RequestUri!.AbsolutePath,
                    StringComparison.Ordinal);
                return JsonResponse(
                    """
                    {
                      "candidates": [{
                        "content": {"parts": [{"text": "{\"ok\":true}"}]},
                        "finishReason": "STOP"
                      }],
                      "usageMetadata": {"totalTokenCount": 15}
                    }
                    """);
            })));

        var result = await client.ProbeAsync(
            Connection() with { ModelId = modelId },
            Encoding.UTF8.GetBytes("test-key"));

        Assert.Equal("passed", result.State);
        Assert.NotNull(body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(expectedThinking, json.RootElement
            .GetProperty("generationConfig")
            .GetProperty("thinkingConfig")
            .GetProperty("thinkingLevel").GetString());
        Assert.Equal(expectedThinking == "LOW" ? 2_048 : 64, json.RootElement
            .GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32());
        var imagePart = json.RootElement.GetProperty("contents")[0]
            .GetProperty("parts").EnumerateArray()
            .Single(part => part.TryGetProperty("inline_data", out _));
        AssertValidProbePng(Convert.FromBase64String(imagePart
            .GetProperty("inline_data").GetProperty("data").GetString()!));
    }

    private static void AssertValidProbePng(byte[] png)
    {
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        Assert.Equal(64u, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)));
        Assert.Equal(64u, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4)));
        using var imageData = new MemoryStream();
        for (var offset = 8; offset < png.Length;)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4)));
            var chunk = png.AsSpan(offset + 4, length + 4);
            var crc = uint.MaxValue;
            foreach (var value in chunk)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
                }
            }

            Assert.Equal(~crc, BinaryPrimitives.ReadUInt32BigEndian(
                png.AsSpan(offset + length + 8, 4)));
            if (chunk[..4].SequenceEqual("IDAT"u8))
            {
                imageData.Write(chunk[4..]);
            }

            offset += length + 12;
            Assert.True(offset <= png.Length);
        }

        imageData.Position = 0;
        using var zlib = new ZLibStream(imageData, CompressionMode.Decompress);
        using var pixels = new MemoryStream();
        zlib.CopyTo(pixels);
        Assert.Equal(64 * (1 + 64 * 3), pixels.Length);
    }

    [Fact]
    public void ApprovedTemplateExtractionBundleIsVersionedAndSourceAware()
    {
        using var catalog = new ApprovedPromptBundleCatalog();
        var bundle = catalog.GetRequired(AiTaskTypes.TemplateExtraction);

        Assert.Equal("template-extract-v2.1.0", bundle.PromptVersion);
        Assert.Equal("template_extract_v6", bundle.SchemaVersion);
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
        Assert.Contains(
            "大問 and 中問 are always scopes and never award points",
            bundle.SystemInstruction,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "it either awards points itself",
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
        Assert.Equal(
            "string",
            questionProperties
                .GetProperty("minor_question_label")
                .GetProperty("type")
                .GetString());
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
