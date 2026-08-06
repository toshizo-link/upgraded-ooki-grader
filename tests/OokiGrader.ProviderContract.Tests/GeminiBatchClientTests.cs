using System.Net;
using System.Text;
using System.Text.Json;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Ai.Gemini;

namespace OokiGrader.ProviderContract.Tests;

public sealed class GeminiBatchClientTests
{
    private const string DisplayName =
        "ooki-01K14P4A2GBB7W4K1M1M1M1M1M-0123456789ab";

    [Fact]
    public void BuildJsonLinesUsesStableKeysAndGemini35SafeConfiguration()
    {
        var client = new GeminiBatchClient(new HttpClient());

        var bytes = client.BuildJsonLines(
            [Request("key-1"), Request("key-2")]);

        var lines = Encoding.UTF8.GetString(bytes)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal("key-1", first.RootElement.GetProperty("key").GetString());
        var request = first.RootElement.GetProperty("request");
        var configuration = request.GetProperty("generationConfig");
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
        Assert.False(configuration.TryGetProperty("topP", out _));
        Assert.False(configuration.TryGetProperty("topK", out _));
        Assert.False(configuration.TryGetProperty("candidateCount", out _));
        Assert.Equal(
            JsonValueKind.String,
            request.GetProperty("contents")[0]
                .GetProperty("parts")[0]
                .GetProperty("inline_data")
                .GetProperty("data")
                .ValueKind);
    }

    [Fact]
    public async Task UploadAndCreateUseOfficialRestEndpointsWithoutRetryHeaders()
    {
        var requests = new List<CapturedRequest>();
        var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.ToDictionary(
                    item => item.Key,
                    item => string.Join(",", item.Value),
                    StringComparer.OrdinalIgnoreCase),
                request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (requests.Count == 1)
            {
                var response = JsonResponse("{}");
                response.Headers.TryAddWithoutValidation(
                    "X-Goog-Upload-URL",
                    "https://generativelanguage.googleapis.com/upload-session/one");
                return response;
            }

            if (requests.Count == 2)
            {
                return JsonResponse(
                    """
                    {
                      "file": {
                        "name": "files/input-1",
                        "uri": "https://generativelanguage.googleapis.com/v1beta/files/input-1",
                        "expirationTime": "2026-07-29T00:00:00Z"
                      }
                    }
                    """);
            }

            return JsonResponse(
                $$"""
                {
                  "name": "batches/batch-1",
                  "metadata": {
                    "displayName": "{{DisplayName}}",
                    "createTime": "2026-07-27T00:00:00Z",
                    "state": "JOB_STATE_PENDING"
                  },
                  "done": false
                }
                """);
        });
        var client = new GeminiBatchClient(new HttpClient(handler));
        var jsonLines = client.BuildJsonLines([Request("key-1")]);

        var file = await client.UploadJsonLinesAsync(
            Connection(),
            Encoding.UTF8.GetBytes("secret-test-key"),
            DisplayName,
            jsonLines);
        var receipt = await client.CreateAsync(
            Connection(),
            Encoding.UTF8.GetBytes("secret-test-key"),
            new AiBatchCreateRequest(
                DisplayName,
                new string('a', 64),
                file.ProviderFileName,
                1));

        Assert.Equal("files/input-1", file.ProviderFileName);
        Assert.Equal("batches/batch-1", receipt.ProviderBatchName);
        Assert.Equal(3, requests.Count);
        Assert.Equal(
            "/upload/v1beta/files",
            requests[0].Uri.AbsolutePath);
        Assert.Equal(
            "resumable",
            requests[0].Headers["X-Goog-Upload-Protocol"]);
        Assert.Equal(
            "upload, finalize",
            requests[1].Headers["X-Goog-Upload-Command"]);
        Assert.Equal(
            "/v1beta/models/gemini-3.5-flash-lite:batchGenerateContent",
            requests[2].Uri.AbsolutePath);
        Assert.DoesNotContain(
            requests[2].Headers.Keys,
            key => key.Contains("idempot", StringComparison.OrdinalIgnoreCase));
        using var createBody = JsonDocument.Parse(requests[2].Body!);
        Assert.Equal(
            DisplayName,
            createBody.RootElement
                .GetProperty("batch")
                .GetProperty("display_name")
                .GetString());
        Assert.Equal(
            "files/input-1",
            createBody.RootElement
                .GetProperty("batch")
                .GetProperty("input_config")
                .GetProperty("file_name")
                .GetString());
    }

    [Fact]
    public async Task CreateClassifiesNetworkLossAfterSendAsAmbiguous()
    {
        var client = new GeminiBatchClient(
            new HttpClient(new DelegateHandler((_, _) =>
                throw new HttpRequestException("synthetic"))));

        var exception = await Assert.ThrowsAsync<AiBatchCreateException>(() =>
            client.CreateAsync(
                Connection(),
                Encoding.UTF8.GetBytes("test-key"),
                new AiBatchCreateRequest(
                    DisplayName,
                    new string('a', 64),
                    "files/input-1",
                    1)));

        Assert.Equal(
            AiBatchCreateFailureKind.AmbiguousAfterSend,
            exception.Kind);
        Assert.Equal(
            "gemini_batch_create_network_unknown",
            exception.SafeErrorCode);
    }

    [Fact]
    public async Task CreateClassifiesHttpRejectionAsDefinite()
    {
        var client = new GeminiBatchClient(
            new HttpClient(new DelegateHandler((_, _) =>
                Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.BadRequest)))));

        var exception = await Assert.ThrowsAsync<AiBatchCreateException>(() =>
            client.CreateAsync(
                Connection(),
                Encoding.UTF8.GetBytes("test-key"),
                new AiBatchCreateRequest(
                    DisplayName,
                    new string('a', 64),
                    "files/input-1",
                    1)));

        Assert.Equal(
            AiBatchCreateFailureKind.DefiniteRemoteRejection,
            exception.Kind);
        Assert.False(exception.IsTransient);
    }

    [Fact]
    public async Task GetAndReadInlineResultsUseMetadataKeyNotArrayPosition()
    {
        var handler = new DelegateHandler((_, _) =>
            Task.FromResult(JsonResponse(
                $$"""
                {
                  "name": "batches/batch-1",
                  "metadata": {
                    "displayName": "{{DisplayName}}",
                    "state": "JOB_STATE_SUCCEEDED",
                    "createTime": "2026-07-27T00:00:00Z"
                  },
                  "done": true,
                  "response": {
                    "inlinedResponses": {
                      "inlinedResponses": [{
                          "metadata": {"key": "key-2"},
                          "response": {
                            "candidates": [{
                              "content": {"parts": [{"text": "{\"ok\":true}"}]},
                              "finishReason": "STOP"
                            }],
                            "usageMetadata": {
                              "promptTokenCount": 10,
                              "candidatesTokenCount": 2,
                              "thoughtsTokenCount": 1,
                              "totalTokenCount": 13
                            },
                            "modelVersion": "gemini-3.5-flash-lite-001",
                            "responseId": "response-2"
                          }
                      }]
                    }
                  }
                }
                """)));
        var client = new GeminiBatchClient(new HttpClient(handler));

        var status = await client.GetAsync(
            Connection(),
            Encoding.UTF8.GetBytes("test-key"),
            "batches/batch-1");
        var results = await client.ReadResultsAsync(
            Connection(),
            Encoding.UTF8.GetBytes("test-key"),
            status);

        Assert.Equal(AiBatchRemoteState.Succeeded, status.State);
        var result = Assert.Single(results);
        Assert.Equal("key-2", result.RequestKey);
        Assert.NotNull(result.Response);
        Assert.True(
            result.Response.StructuredOutput
                .GetProperty("ok")
                .GetBoolean());
        Assert.Equal(13, result.Response.Usage.TotalTokens);
    }

    [Fact]
    public async Task GetRecognizesOfficialTopLevelResponsesFile()
    {
        var handler = new DelegateHandler((_, _) =>
            Task.FromResult(JsonResponse(
                $$"""
                {
                  "name": "batches/batch-1",
                  "metadata": {
                    "displayName": "{{DisplayName}}",
                    "state": "BATCH_STATE_SUCCEEDED"
                  },
                  "done": true,
                  "response": {
                    "responsesFile": "files/output-official"
                  }
                }
                """)));
        var client = new GeminiBatchClient(new HttpClient(handler));

        var status = await client.GetAsync(
            Connection(),
            Encoding.UTF8.GetBytes("test-key"),
            "batches/batch-1");

        Assert.Equal(AiBatchRemoteState.Succeeded, status.State);
        Assert.Equal("files/output-official", status.OutputFileName);
    }

    [Fact]
    public async Task CancelUsesOfficialBatchCancelMethod()
    {
        HttpMethod? observedMethod = null;
        string? observedPath = null;
        string? observedKey = null;
        var handler = new DelegateHandler((request, _) =>
        {
            observedMethod = request.Method;
            observedPath = request.RequestUri?.AbsolutePath;
            observedKey = request.Headers.GetValues("x-goog-api-key").Single();
            return Task.FromResult(JsonResponse("{}"));
        });
        var client = new GeminiBatchClient(new HttpClient(handler));

        await client.CancelAsync(
            Connection(),
            Encoding.UTF8.GetBytes("test-key"),
            "batches/batch-1");

        Assert.Equal(HttpMethod.Post, observedMethod);
        Assert.Equal(
            "/v1beta/batches/batch-1:cancel",
            observedPath);
        Assert.Equal("test-key", observedKey);
    }

    [Fact]
    public async Task GetRecognizesOfficialCancelledOperationError()
    {
        var handler = new DelegateHandler((_, _) =>
            Task.FromResult(JsonResponse(
                """
                {
                  "name": "batches/batch-1",
                  "done": true,
                  "error": {
                    "code": 1,
                    "message": "cancelled"
                  }
                }
                """)));
        var client = new GeminiBatchClient(new HttpClient(handler));

        var status = await client.GetAsync(
            Connection(),
            Encoding.UTF8.GetBytes("test-key"),
            "batches/batch-1");

        Assert.Equal(AiBatchRemoteState.Cancelled, status.State);
    }

    [Fact]
    public async Task DeleteBatchUsesOfficialOperationDeleteMethod()
    {
        HttpMethod? observedMethod = null;
        string? observedPath = null;
        var handler = new DelegateHandler((request, _) =>
        {
            observedMethod = request.Method;
            observedPath = request.RequestUri?.AbsolutePath;
            return Task.FromResult(JsonResponse("{}"));
        });
        var client = new GeminiBatchClient(new HttpClient(handler));

        await client.DeleteBatchAsync(
            Connection(),
            Encoding.UTF8.GetBytes("test-key"),
            "batches/batch-1");

        Assert.Equal(HttpMethod.Delete, observedMethod);
        Assert.Equal("/v1beta/batches/batch-1", observedPath);
    }

    [Fact]
    public async Task ReadFileResultsGetsDownloadUriAndParsesJsonLines()
    {
        var calls = 0;
        var handler = new DelegateHandler((request, _) =>
        {
            calls++;
            if (request.RequestUri!.AbsolutePath == "/v1beta/files/output-1")
            {
                return Task.FromResult(JsonResponse(
                    """
                    {
                      "name": "files/output-1",
                      "downloadUri": "https://generativelanguage.googleapis.com/download/v1beta/files/output-1:download?alt=media"
                    }
                    """));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"key":"key-1","response":{"candidates":[{"content":{"parts":[{"text":"{\"ok\":true}"}]},"finishReason":"STOP"}],"usageMetadata":{"totalTokenCount":3},"responseId":"r1"}}

                    """,
                    Encoding.UTF8,
                    "application/jsonl"),
            });
        });
        var client = new GeminiBatchClient(new HttpClient(handler));
        using var raw = JsonDocument.Parse(
            """
            {
              "name":"batches/batch-1",
              "metadata":{"state":"JOB_STATE_SUCCEEDED"}
            }
            """);
        var status = new AiBatchStatus(
            "batches/batch-1",
            DisplayName,
            AiBatchRemoteState.Succeeded,
            null,
            null,
            null,
            null,
            "files/output-1",
            null,
            raw.RootElement.Clone());

        var results = await client.ReadResultsAsync(
            Connection(),
            Encoding.UTF8.GetBytes("test-key"),
            status);

        Assert.Equal(2, calls);
        var item = Assert.Single(results);
        Assert.Equal("key-1", item.RequestKey);
        Assert.Equal("r1", item.Response!.ProviderResponseId);
    }

    [Fact]
    public async Task BatchClientRejectsUnapprovedModelBeforeNetworkCall()
    {
        var calls = 0;
        var client = new GeminiBatchClient(
            new HttpClient(new DelegateHandler((_, _) =>
            {
                calls++;
                return Task.FromResult(JsonResponse("{}"));
            })));

        await Assert.ThrowsAsync<AiProviderException>(() =>
            client.GetAsync(
                Connection() with { ModelId = "gemini-3.6-flash" },
                Encoding.UTF8.GetBytes("test-key"),
                "batches/batch-1"));

        Assert.Equal(0, calls);
    }

    private static AiProviderRequest Request(string requestKey)
    {
        using var schema = JsonDocument.Parse(
            """{"type":"object","additionalProperties":false,"properties":{"ok":{"type":"boolean"}},"required":["ok"]}""");
        var media = Encoding.UTF8.GetBytes("synthetic-image");
        return new AiProviderRequest(
            requestKey,
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
            ]);
    }

    private static AiConnectionSettings Connection() =>
        new(
            "connection-1",
            AiProviders.GeminiDirect,
            new Uri("https://generativelanguage.googleapis.com/"),
            GeminiBatchClient.SelectedModel,
            TimeSpan.FromSeconds(30));

    private static HttpResponseMessage JsonResponse(string value) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri Uri,
        Dictionary<string, string> Headers,
        string? Body);

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
