using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Ai.OpenRouter;

namespace OokiGrader.ProviderContract.Tests;

public sealed class OpenRouterClientTests
{
    private const string TestCredential = "sk-or-v1-test-credential";

    [Fact]
    public async Task GenerateAsyncSendsStrictStructuredMultimodalRequest()
    {
        HttpMethod? method = null;
        Uri? requestUri = null;
        AuthenticationHeaderValue? authorization = null;
        string? body = null;
        var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            method = request.Method;
            requestUri = request.RequestUri;
            authorization = request.Headers.Authorization;
            body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse(
                """
                {
                  "id": "generation-1",
                  "model": "google/gemini-3.1-flash-lite",
                  "provider": "Google AI Studio",
                  "choices": [{
                    "index": 0,
                    "message": {
                      "role": "assistant",
                      "content": "{\"ok\":true}"
                    },
                    "finish_reason": "stop"
                  }],
                  "usage": {
                    "prompt_tokens": 21,
                    "completion_tokens": 5,
                    "total_tokens": 26,
                    "cost": 0.0000121,
                    "prompt_tokens_details": {"cached_tokens": 3},
                    "completion_tokens_details": {"reasoning_tokens": 2}
                  }
                }
                """);
        });
        var client = new OpenRouterClient(new HttpClient(handler));
        var image = Encoding.UTF8.GetBytes("synthetic-image");
        var pdf = Encoding.UTF8.GetBytes("synthetic-pdf");

        var response = await client.GenerateAsync(
            Connection(),
            Encoding.ASCII.GetBytes(TestCredential),
            Request(
                Media("image/png", image),
                Media("application/pdf", pdf)));

        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal(
            new Uri("https://openrouter.ai/api/v1/chat/completions"),
            requestUri);
        Assert.NotNull(authorization);
        Assert.Equal("Bearer", authorization.Scheme);
        Assert.Equal(TestCredential, authorization.Parameter);

        Assert.Equal(AiProviders.OpenRouter, response.Provider);
        Assert.Equal("google/gemini-3.1-flash-lite", response.RequestedModel);
        Assert.Equal("google/gemini-3.1-flash-lite", response.ActualModel);
        Assert.Equal("generation-1", response.ProviderResponseId);
        Assert.Equal("stop", response.FinishReason);
        Assert.Equal("Google AI Studio", response.RoutedProvider);
        Assert.True(response.StructuredOutput.GetProperty("ok").GetBoolean());
        Assert.Equal(21, response.Usage.PromptTokens);
        Assert.Equal(3, response.Usage.CachedTokens);
        Assert.Equal(3, response.Usage.OutputTokens);
        Assert.Equal(2, response.Usage.ThinkingTokens);
        Assert.Equal(26, response.Usage.TotalTokens);
        Assert.Equal(13, response.Usage.ProviderCostUsdMicros);

        Assert.NotNull(body);
        Assert.DoesNotContain(
            TestCredential,
            body,
            StringComparison.Ordinal);
        using var requestJson = JsonDocument.Parse(body);
        var root = requestJson.RootElement;
        Assert.Equal(
            "google/gemini-3.1-flash-lite",
            root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal(8_192, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal(
            "minimal",
            root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.True(
            root.GetProperty("reasoning").GetProperty("exclude").GetBoolean());
        Assert.False(root.TryGetProperty("tools", out _));
        Assert.False(root.TryGetProperty("plugins", out _));
        Assert.False(root.TryGetProperty("models", out _));

        var messages = root.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("system", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        var content = messages[1].GetProperty("content");
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("user", content[0].GetProperty("text").GetString());
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.Equal(
            $"data:image/png;base64,{Convert.ToBase64String(image)}",
            content[1]
                .GetProperty("image_url")
                .GetProperty("url")
                .GetString());
        Assert.Equal("file", content[2].GetProperty("type").GetString());
        Assert.Equal(
            $"data:application/pdf;base64,{Convert.ToBase64String(pdf)}",
            content[2]
                .GetProperty("file")
                .GetProperty("file_data")
                .GetString());

        var responseFormat = root.GetProperty("response_format");
        Assert.Equal(
            "json_schema",
            responseFormat.GetProperty("type").GetString());
        var jsonSchema = responseFormat.GetProperty("json_schema");
        Assert.Equal(
            "ooki_grader_response",
            jsonSchema.GetProperty("name").GetString());
        Assert.True(jsonSchema.GetProperty("strict").GetBoolean());
        Assert.False(
            jsonSchema
                .GetProperty("schema")
                .GetProperty("additionalProperties")
                .GetBoolean());
        Assert.True(
            root.GetProperty("provider")
                .GetProperty("require_parameters")
                .GetBoolean());
        Assert.Equal(
            "deny",
            root.GetProperty("provider")
                .GetProperty("data_collection")
                .GetString());
        Assert.True(
            root.GetProperty("provider")
                .GetProperty("zdr")
                .GetBoolean());
    }

    [Fact]
    public async Task GenerateAsyncClosesEveryObjectSchemaForStrictOutput()
    {
        string? body = null;
        var client = new OpenRouterClient(new HttpClient(new DelegateHandler(
            async (request, cancellationToken) =>
            {
                body = await request.Content!.ReadAsStringAsync(cancellationToken);
                return JsonResponse(
                    """
                    {
                      "id": "generation-strict",
                      "model": "google/gemini-3.1-flash-lite",
                      "choices": [{
                        "message": {
                          "role": "assistant",
                          "content": "{\"child\":{\"value\":true}}"
                        },
                        "finish_reason": "stop"
                      }]
                    }
                    """);
            })));
        using var schema = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "child": {
                  "type": "object",
                  "properties": {"value": {"type": "boolean"}},
                  "required": ["value"]
                }
              },
              "required": ["child"]
            }
            """);

        await client.GenerateAsync(
            Connection(),
            Encoding.ASCII.GetBytes(TestCredential),
            Request(Media("image/png", [1])) with
            {
                ResponseJsonSchema = schema.RootElement.Clone(),
            });

        Assert.NotNull(body);
        using var requestJson = JsonDocument.Parse(body);
        var strictSchema = requestJson.RootElement
            .GetProperty("response_format")
            .GetProperty("json_schema")
            .GetProperty("schema");
        Assert.False(strictSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.False(
            strictSchema
                .GetProperty("properties")
                .GetProperty("child")
                .GetProperty("additionalProperties")
                .GetBoolean());
    }

    [Theory]
    [InlineData("MINIMAL", "minimal")]
    [InlineData("LOW", "low")]
    [InlineData("MEDIUM", "medium")]
    [InlineData("HIGH", "high")]
    public async Task GenerateAsyncMapsApprovedThinkingLevelToReasoningEffort(
        string thinkingLevel,
        string expectedEffort)
    {
        string? body = null;
        var client = new OpenRouterClient(new HttpClient(new DelegateHandler(
            async (request, cancellationToken) =>
            {
                body = await request.Content!.ReadAsStringAsync(cancellationToken);
                return JsonResponse(
                    """
                    {
                      "id": "generation-reasoning",
                      "model": "google/gemini-3.1-flash-lite",
                      "choices": [{
                        "message": {"role": "assistant", "content": "{\"ok\":true}"},
                        "finish_reason": "stop"
                      }]
                    }
                    """);
            })));

        await client.GenerateAsync(
            Connection(),
            Encoding.ASCII.GetBytes(TestCredential),
            Request(Media("image/png", [1])) with
            {
                ThinkingLevel = thinkingLevel,
            });

        Assert.NotNull(body);
        Assert.DoesNotContain(TestCredential, body, StringComparison.Ordinal);
        using var requestJson = JsonDocument.Parse(body);
        var reasoning = requestJson.RootElement.GetProperty("reasoning");
        Assert.Equal(expectedEffort, reasoning.GetProperty("effort").GetString());
        Assert.True(reasoning.GetProperty("exclude").GetBoolean());
    }

    [Theory]
    [InlineData("")]
    [InlineData("minimal")]
    [InlineData("UNSPECIFIED")]
    [InlineData("HIGH\r\nleak")]
    public async Task GenerateAsyncRejectsUnapprovedThinkingLevelBeforeSending(
        string thinkingLevel)
    {
        var calls = 0;
        var client = ClientThatCountsCalls(() => calls++);

        var exception = await Assert.ThrowsAsync<AiProviderException>(() =>
            client.GenerateAsync(
                Connection(),
                Encoding.ASCII.GetBytes(TestCredential),
                Request(Media("image/png", [1])) with
                {
                    ThinkingLevel = thinkingLevel,
                }));

        Assert.Equal(AiFailureKind.RequestRejected, exception.Kind);
        Assert.Equal("ai_request_invalid", exception.SafeErrorCode);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("http://openrouter.ai/api/v1/")]
    [InlineData("https://openrouter.ai.evil.test/api/v1/")]
    [InlineData("https://openrouter.ai:8443/api/v1/")]
    [InlineData("https://user@openrouter.ai/api/v1/")]
    [InlineData("https://openrouter.ai/api/v2/")]
    [InlineData("https://openrouter.ai/api/v1/?target=elsewhere")]
    public async Task GenerateAsyncRejectsNonCanonicalEndpointBeforeSending(
        string endpoint)
    {
        var calls = 0;
        var client = ClientThatCountsCalls(() => calls++);

        var exception = await Assert.ThrowsAsync<AiProviderException>(() =>
            client.GenerateAsync(
                Connection() with { BaseAddress = new Uri(endpoint) },
                Encoding.ASCII.GetBytes(TestCredential),
                Request(Media("image/png", [1]))));

        Assert.Equal(AiFailureKind.InvalidConfiguration, exception.Kind);
        Assert.Equal("openrouter_configuration_invalid", exception.SafeErrorCode);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("google/gemini-3.1-flash-lite")]
    [InlineData("openai/gpt-4o:free")]
    public void ProviderCatalogAcceptsBoundedOpenRouterSlugs(string modelId)
    {
        Assert.True(AiProviderCatalog.IsModelIdValid(
            AiProviders.OpenRouter,
            modelId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("gemini-3.1-flash-lite")]
    [InlineData("google//gemini")]
    [InlineData("google/gemini/latest")]
    [InlineData("google/gemini 3")]
    [InlineData("../google/gemini")]
    [InlineData("https://openrouter.ai/google/gemini")]
    [InlineData("openrouter/auto")]
    [InlineData("openai/gpt-4o:online")]
    public void ProviderCatalogRejectsUnsafeOrNonSlugModelIds(string modelId)
    {
        Assert.False(AiProviderCatalog.IsModelIdValid(
            AiProviders.OpenRouter,
            modelId));
    }

    [Theory]
    [InlineData("deepseek/deepseek-v4-flash")]
    [InlineData("deepseek/deepseek-v4-flash-0731")]
    public void ProviderCatalogRejectsDeepSeekV4FlashFamilyForImageTasks(
        string modelId)
    {
        Assert.True(AiProviderCatalog.IsModelIdValid(
            AiProviders.OpenRouter,
            modelId));
        Assert.False(AiProviderCatalog.SupportsImageTasks(
            AiProviders.OpenRouter,
            modelId));
    }

    [Theory]
    [InlineData("too-short")]
    [InlineData("sk-or-v1-valid-prefix\r\ninjected: true")]
    [InlineData("sk-or-v1-valid prefix with spaces")]
    public async Task GenerateAsyncRejectsInvalidCredentialBeforeSending(
        string credential)
    {
        var calls = 0;
        var client = ClientThatCountsCalls(() => calls++);

        var exception = await Assert.ThrowsAsync<AiProviderException>(() =>
            client.GenerateAsync(
                Connection(),
                Encoding.ASCII.GetBytes(credential),
                Request(Media("image/png", [1]))));

        Assert.Equal(AiFailureKind.Authentication, exception.Kind);
        Assert.Equal("openrouter_credential_invalid", exception.SafeErrorCode);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(401, AiFailureKind.Authentication, false)]
    [InlineData(402, AiFailureKind.BudgetBlocked, false)]
    [InlineData(403, AiFailureKind.SafetyBlocked, false)]
    [InlineData(404, AiFailureKind.InvalidConfiguration, false)]
    [InlineData(408, AiFailureKind.Timeout, true)]
    [InlineData(413, AiFailureKind.RequestRejected, false)]
    [InlineData(429, AiFailureKind.RateLimited, true)]
    [InlineData(502, AiFailureKind.TransientProvider, true)]
    [InlineData(503, AiFailureKind.TransientProvider, true)]
    [InlineData(524, AiFailureKind.Timeout, true)]
    [InlineData(529, AiFailureKind.TransientProvider, true)]
    public async Task GenerateAsyncClassifiesHttpErrors(
        int statusCode,
        AiFailureKind expectedKind,
        bool expectedTransient)
    {
        var retryAfter = TimeSpan.FromSeconds(7);
        var client = new OpenRouterClient(new HttpClient(new DelegateHandler((_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)statusCode)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        error = new
                        {
                            code = statusCode,
                            message = "sensitive provider detail",
                        },
                    }),
                    Encoding.UTF8,
                    "application/json"),
            };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
            return Task.FromResult(response);
        })));

        var exception = await Assert.ThrowsAsync<AiProviderException>(() =>
            client.GenerateAsync(
                Connection(),
                Encoding.ASCII.GetBytes(TestCredential),
                Request(Media("image/png", [1]))));

        Assert.Equal(expectedKind, exception.Kind);
        Assert.Equal(expectedTransient, exception.IsTransient);
        Assert.Equal(
            expectedTransient ? retryAfter : null,
            exception.RetryAfter);
        Assert.DoesNotContain(
            "sensitive provider detail",
            exception.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            TestCredential,
            exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsyncClassifiesErrorInsideSuccessfulEnvelope()
    {
        var client = new OpenRouterClient(new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(JsonResponse(
                """
                {
                  "id": "generation-error",
                  "model": "google/gemini-3.1-flash-lite",
                  "choices": [{
                    "message": {"role": "assistant", "content": "partial"},
                    "finish_reason": "error",
                    "error": {
                      "code": 429,
                      "message": "rate limited after dispatch"
                    }
                  }]
                }
                """)))));

        var exception = await Assert.ThrowsAsync<AiProviderException>(() =>
            client.GenerateAsync(
                Connection(),
                Encoding.ASCII.GetBytes(TestCredential),
                Request(Media("image/png", [1]))));

        Assert.Equal(AiFailureKind.RateLimited, exception.Kind);
        Assert.Equal("openrouter_rate_limited", exception.SafeErrorCode);
        Assert.True(exception.IsTransient);
    }

    [Theory]
    [InlineData(
        "length",
        "{\"ok\":true}",
        AiFailureKind.InvalidResponse,
        "openrouter_finish_reason_invalid")]
    [InlineData(
        "content_filter",
        "{\"ok\":true}",
        AiFailureKind.SafetyBlocked,
        "openrouter_output_blocked")]
    [InlineData(
        "stop",
        "```json\\n{\"ok\":true}\\n```",
        AiFailureKind.InvalidResponse,
        "openrouter_json_invalid")]
    [InlineData(
        "stop",
        "[]",
        AiFailureKind.InvalidResponse,
        "openrouter_structured_output_invalid")]
    public async Task GenerateAsyncRejectsNonAtomicOrNonObjectOutput(
        string finishReason,
        string content,
        AiFailureKind expectedKind,
        string expectedCode)
    {
        var envelope = JsonSerializer.Serialize(new
        {
            id = "generation-invalid",
            model = "google/gemini-3.1-flash-lite",
            choices = new[]
            {
                new
                {
                    message = new { role = "assistant", content },
                    finish_reason = finishReason,
                },
            },
        });
        var client = new OpenRouterClient(new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(JsonResponse(envelope)))));

        var exception = await Assert.ThrowsAsync<AiProviderException>(() =>
            client.GenerateAsync(
                Connection(),
                Encoding.ASCII.GetBytes(TestCredential),
                Request(Media("image/png", [1]))));

        Assert.Equal(expectedKind, exception.Kind);
        Assert.Equal(expectedCode, exception.SafeErrorCode);
    }

    [Fact]
    public async Task GenerateAsyncRejectsMismatchedResponseModelWithoutLeakingIt()
    {
        const string unapprovedModel = "sensitive-provider/unapproved-model";
        const string sensitiveContent = "sensitive response content";
        var client = new OpenRouterClient(new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(JsonResponse(
                $$"""
                {
                  "id": "generation-wrong-model",
                  "model": "{{unapprovedModel}}",
                  "choices": [{
                    "message": {
                      "role": "assistant",
                      "content": "{\"ok\":true,\"detail\":\"{{sensitiveContent}}\"}"
                    },
                    "finish_reason": "stop"
                  }]
                }
                """)))));

        var exception = await Assert.ThrowsAsync<AiProviderException>(() =>
            client.GenerateAsync(
                Connection(),
                Encoding.ASCII.GetBytes(TestCredential),
                Request(Media("image/png", [1]))));

        Assert.Equal(AiFailureKind.InvalidResponse, exception.Kind);
        Assert.Equal(
            "openrouter_response_metadata_invalid",
            exception.SafeErrorCode);
        Assert.DoesNotContain(
            unapprovedModel,
            exception.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            sensitiveContent,
            exception.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            TestCredential,
            exception.ToString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" provider-with-leading-space")]
    [InlineData("provider\r\nsecret-header")]
    public async Task GenerateAsyncRejectsUnsafeRoutedProviderWithoutLeakingIt(
        string routedProvider)
    {
        var envelope = JsonSerializer.Serialize(new
        {
            id = "generation-unsafe-route",
            model = "google/gemini-3.1-flash-lite",
            provider = routedProvider,
            choices = new[]
            {
                new
                {
                    message = new { role = "assistant", content = "{\"ok\":true}" },
                    finish_reason = "stop",
                },
            },
        });
        var client = new OpenRouterClient(new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(JsonResponse(envelope)))));

        var exception = await Assert.ThrowsAsync<AiProviderException>(() =>
            client.GenerateAsync(
                Connection(),
                Encoding.ASCII.GetBytes(TestCredential),
                Request(Media("image/png", [1]))));

        Assert.Equal(AiFailureKind.InvalidResponse, exception.Kind);
        Assert.Equal(
            "openrouter_response_metadata_invalid",
            exception.SafeErrorCode);
        if (routedProvider.Length > 0)
        {
            Assert.DoesNotContain(
                routedProvider,
                exception.ToString(),
                StringComparison.Ordinal);
        }
        Assert.DoesNotContain(
            TestCredential,
            exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsyncRejectsOversizedRoutedProviderWithoutLeakingIt()
    {
        var routedProvider = new string('p', 65);
        var envelope = JsonSerializer.Serialize(new
        {
            id = "generation-oversized-route",
            model = "google/gemini-3.1-flash-lite",
            provider = routedProvider,
            choices = new[]
            {
                new
                {
                    message = new { role = "assistant", content = "{\"ok\":true}" },
                    finish_reason = "stop",
                },
            },
        });
        var client = new OpenRouterClient(new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(JsonResponse(envelope)))));

        var exception = await Assert.ThrowsAsync<AiProviderException>(() =>
            client.GenerateAsync(
                Connection(),
                Encoding.ASCII.GetBytes(TestCredential),
                Request(Media("image/png", [1]))));

        Assert.Equal(
            "openrouter_response_metadata_invalid",
            exception.SafeErrorCode);
        Assert.DoesNotContain(
            routedProvider,
            exception.ToString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenerateAsyncKeepsMissingOrNullProviderCostUnknown(
        bool includeNullCost)
    {
        var costMember = includeNullCost ? "\"cost\": null," : string.Empty;
        var client = new OpenRouterClient(new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(JsonResponse(
                $$"""
                {
                  "id": "generation-cost-unknown",
                  "model": "google/gemini-3.1-flash-lite",
                  "choices": [{
                    "message": {"role": "assistant", "content": "{\"ok\":true}"},
                    "finish_reason": "stop"
                  }],
                  "usage": {
                    {{costMember}}
                    "prompt_tokens": 4,
                    "completion_tokens": 3,
                    "total_tokens": 7
                  }
                }
                """)))));

        var response = await client.GenerateAsync(
            Connection(),
            Encoding.ASCII.GetBytes(TestCredential),
            Request(Media("image/png", [1])));

        Assert.Null(response.Usage.ProviderCostUsdMicros);
        Assert.Equal(3, response.Usage.OutputTokens);
    }

    [Fact]
    public async Task GenerateAsyncPreservesExplicitZeroProviderCost()
    {
        var client = new OpenRouterClient(new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(JsonResponse(
                """
                {
                  "id": "generation-free-cost",
                  "model": "google/gemini-3.1-flash-lite",
                  "choices": [{
                    "message": {"role": "assistant", "content": "{\"ok\":true}"},
                    "finish_reason": "stop"
                  }],
                  "usage": {
                    "prompt_tokens": 4,
                    "completion_tokens": 3,
                    "total_tokens": 7,
                    "cost": 0
                  }
                }
                """)))));

        var response = await client.GenerateAsync(
            Connection(),
            Encoding.ASCII.GetBytes(TestCredential),
            Request(Media("image/png", [1])));

        Assert.Equal(0, response.Usage.ProviderCostUsdMicros);
    }

    [Fact]
    public async Task GenerateAsyncRejectsReasoningCountOutsideCompletionTotal()
    {
        var client = new OpenRouterClient(new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(JsonResponse(
                """
                {
                  "id": "generation-invalid-usage",
                  "model": "google/gemini-3.1-flash-lite",
                  "choices": [{
                    "message": {"role": "assistant", "content": "{\"ok\":true}"},
                    "finish_reason": "stop"
                  }],
                  "usage": {
                    "prompt_tokens": 4,
                    "completion_tokens": 2,
                    "total_tokens": 6,
                    "cost": 0.00001,
                    "completion_tokens_details": {"reasoning_tokens": 3}
                  }
                }
                """)))));

        var exception = await Assert.ThrowsAsync<AiProviderException>(() =>
            client.GenerateAsync(
                Connection(),
                Encoding.ASCII.GetBytes(TestCredential),
                Request(Media("image/png", [1]))));

        Assert.Equal(AiFailureKind.InvalidResponse, exception.Kind);
        Assert.Equal("openrouter_usage_invalid", exception.SafeErrorCode);
    }

    [Fact]
    public async Task ProbeAsyncRejectsUsageWithoutAuthoritativeProviderCost()
    {
        var calls = 0;
        var client = new OpenRouterClient(new HttpClient(new DelegateHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(JsonResponse(
                """
                {
                  "id": "probe-no-cost",
                  "model": "google/gemini-3.1-flash-lite",
                  "choices": [{
                    "message": {"role": "assistant", "content": "{\"ok\":true}"},
                    "finish_reason": "stop"
                  }],
                  "usage": {
                    "prompt_tokens": 4,
                    "completion_tokens": 3,
                    "total_tokens": 7
                  }
                }
                """));
        })));

        var result = await client.ProbeAsync(
            Connection(),
            Encoding.ASCII.GetBytes(TestCredential));

        Assert.Equal(1, calls);
        Assert.True(result.Authentication);
        Assert.True(result.ModelAvailable);
        Assert.False(result.ImageInput);
        Assert.True(result.StructuredOutput);
        Assert.False(result.UsageMetadata);
        Assert.Equal("failed", result.State);
        Assert.Equal("openrouter_usage_metadata_missing", result.SafeErrorCode);
    }

    [Fact]
    public async Task ProbeAsyncRequiresImageStructuredOutputAndUsage()
    {
        var bodies = new List<string>();
        var client = new OpenRouterClient(new HttpClient(new DelegateHandler(
            async (request, cancellationToken) =>
            {
                bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                return JsonResponse(
                    """
                    {
                      "id": "probe-1",
                      "model": "google/gemini-3.1-flash-lite",
                      "choices": [{
                        "message": {"role": "assistant", "content": "{\"ok\":true}"},
                        "finish_reason": "stop"
                      }],
                      "usage": {
                        "prompt_tokens": 4,
                        "completion_tokens": 3,
                        "total_tokens": 7,
                        "cost": 0.000007
                      }
                    }
                    """);
            })));

        var result = await client.ProbeAsync(
            Connection(),
            Encoding.ASCII.GetBytes(TestCredential));

        Assert.True(result.Authentication);
        Assert.True(result.ModelAvailable);
        Assert.True(result.ImageInput);
        Assert.True(result.StructuredOutput);
        Assert.True(result.UsageMetadata);
        Assert.Equal("passed", result.State);
        Assert.Null(result.SafeErrorCode);
        Assert.Equal(2, bodies.Count);
        using var textRequest = JsonDocument.Parse(bodies[0]);
        Assert.Single(
            textRequest.RootElement
                .GetProperty("messages")[1]
                .GetProperty("content")
                .EnumerateArray());
        using var requestJson = JsonDocument.Parse(bodies[1]);
        Assert.StartsWith(
            "data:image/png;base64,",
            requestJson.RootElement
                .GetProperty("messages")[1]
                .GetProperty("content")[1]
                .GetProperty("image_url")
                .GetProperty("url")
                .GetString() ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("deepseek/deepseek-v4-flash")]
    [InlineData("deepseek/deepseek-v4-flash-0731")]
    public async Task ProbeAsyncPreservesTextCapabilitiesWhenImageIsUnsupported(
        string modelId)
    {
        var call = 0;
        var client = new OpenRouterClient(new HttpClient(new DelegateHandler((_, _) =>
        {
            call++;
            if (call == 1)
            {
                return Task.FromResult(JsonResponse(
                    $$"""
                    {
                      "id": "probe-text",
                      "model": "{{modelId}}",
                      "choices": [{
                        "message": {"role": "assistant", "content": "{\"ok\":true}"},
                        "finish_reason": "stop"
                      }],
                      "usage": {
                        "prompt_tokens": 4,
                        "completion_tokens": 3,
                        "total_tokens": 7,
                        "cost": 0.000007
                      }
                    }
                    """));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(
                    """
                    {
                      "error": {
                        "code": 404,
                        "message": "No endpoints found that support image input"
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            });
        })));

        var result = await client.ProbeAsync(
            Connection() with { ModelId = modelId },
            Encoding.ASCII.GetBytes(TestCredential));

        Assert.Equal(2, call);
        Assert.True(result.Authentication);
        Assert.True(result.ModelAvailable);
        Assert.False(result.ImageInput);
        Assert.True(result.StructuredOutput);
        Assert.True(result.UsageMetadata);
        Assert.Equal("failed", result.State);
        Assert.Equal("openrouter_image_not_supported", result.SafeErrorCode);
    }

    private static OpenRouterClient ClientThatCountsCalls(Action onCall) =>
        new(new HttpClient(new DelegateHandler((_, _) =>
        {
            onCall();
            return Task.FromResult(JsonResponse("{}"));
        })));

    private static AiConnectionSettings Connection() =>
        new(
            "connection-1",
            AiProviders.OpenRouter,
            AiProviderCatalog.OpenRouterBaseAddress,
            "google/gemini-3.1-flash-lite",
            TimeSpan.FromSeconds(30));

    private static AiProviderRequest Request(params AiMediaPart[] media)
    {
        using var schema = JsonDocument.Parse(
            """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {"ok": {"type": "boolean"}},
              "required": ["ok"]
            }
            """);
        return new AiProviderRequest(
            "request-1",
            AiTaskTypes.InitialGrading,
            "prompt-v1",
            "schema-v1",
            "system",
            "user",
            schema.RootElement.Clone(),
            media);
    }

    private static AiMediaPart Media(string mimeType, byte[] bytes) =>
        new(
            mimeType,
            bytes,
            Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(bytes))
                .ToLowerInvariant());

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
