using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OokiGrader.Ai.Abstractions;

namespace OokiGrader.Ai.Gemini;

public sealed partial class GeminiDirectClient(HttpClient httpClient) : IAiProviderClient
{
    private const int MaximumInlineRequestBytes = 18 * 1024 * 1024;
    private const int MaximumResponseBytes = 8 * 1024 * 1024;
    private const int MaximumErrorResponseBytes = 64 * 1024;
    private const string AllowedHost = "generativelanguage.googleapis.com";
    private static readonly byte[] ProbePng =
    [
        137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82,
        0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0, 31, 21, 196, 137,
        0, 0, 0, 13, 73, 68, 65, 84, 8, 215, 99, 248, 255, 255, 255,
        127, 0, 9, 251, 3, 253, 42, 134, 227, 138, 0, 0, 0, 0, 73,
        69, 78, 68, 174, 66, 96, 130,
    ];

    public string Provider => AiProviders.GeminiDirect;

    public async Task<AiProviderResponse> GenerateAsync(
        AiConnectionSettings connection,
        ReadOnlyMemory<byte> credentialUtf8,
        AiProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateConnection(connection);
        ValidateRequest(request);
        if (credentialUtf8.IsEmpty)
        {
            throw Failure(
                AiFailureKind.Authentication,
                "gemini_credential_missing",
                isTransient: false);
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        linkedCancellation.CancelAfter(connection.Timeout);
        var started = Stopwatch.GetTimestamp();

        try
        {
            using var message = new HttpRequestMessage(
                HttpMethod.Post,
                BuildGenerateUri(connection));
            message.Headers.TryAddWithoutValidation(
                "x-goog-api-key",
                Encoding.UTF8.GetString(credentialUtf8.Span));
            message.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            message.Content = new ByteArrayContent(BuildRequestBody(request));
            message.Content.Headers.ContentType =
                new MediaTypeHeaderValue("application/json")
                {
                    CharSet = "utf-8",
                };

            using var response = await httpClient.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    linkedCancellation.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw await FromHttpStatusAsync(
                        response,
                        linkedCancellation.Token)
                    .ConfigureAwait(false);
            }

            var responseBytes = await ReadBoundedAsync(
                    response.Content,
                    MaximumResponseBytes,
                    linkedCancellation.Token)
                .ConfigureAwait(false);
            return ParseResponse(
                connection,
                responseBytes,
                Stopwatch.GetElapsedTime(started));
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiProviderException(
                AiFailureKind.Timeout,
                "gemini_timeout",
                isTransient: true,
                innerException: exception);
        }
        catch (HttpRequestException exception)
        {
            throw new AiProviderException(
                AiFailureKind.TransientProvider,
                "gemini_network_error",
                isTransient: true,
                innerException: exception);
        }
    }

    public async Task<AiCapabilityProbeResult> ProbeAsync(
        AiConnectionSettings connection,
        ReadOnlyMemory<byte> credentialUtf8,
        CancellationToken cancellationToken = default)
    {
        using var schema = JsonDocument.Parse(
            """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "ok": { "type": "boolean" }
              },
              "required": ["ok"]
            }
            """);
        var probe = new AiProviderRequest(
            $"probe_{Guid.NewGuid():N}",
            "capabilityProbe",
            "capability-probe-v1",
            "capability-probe-v1",
            "The image is synthetic test data. Output only the requested schema.",
            "Inspect the one-pixel image and return {\"ok\":true}.",
            schema.RootElement.Clone(),
            [
                new AiMediaPart(
                    "image/png",
                    ProbePng,
                    Convert.ToHexString(
                            System.Security.Cryptography.SHA256.HashData(ProbePng))
                        .ToLowerInvariant()),
            ],
            MaxOutputTokens: 64,
            MediaResolution: "MEDIA_RESOLUTION_LOW");

        try
        {
            var response = await GenerateAsync(
                    connection,
                    credentialUtf8,
                    probe,
                    cancellationToken)
                .ConfigureAwait(false);
            var accepted = response.StructuredOutput.TryGetProperty(
                    "ok",
                    out var ok)
                && ok.ValueKind is JsonValueKind.True;
            return new AiCapabilityProbeResult(
                Authentication: true,
                ModelAvailable: true,
                ImageInput: accepted,
                StructuredOutput: accepted,
                UsageMetadata: response.Usage.TotalTokens is not null,
                State: accepted ? "passed" : "failed",
                SafeErrorCode: accepted ? null : "gemini_probe_value_invalid",
                Latency: response.Latency);
        }
        catch (AiProviderException exception)
        {
            return new AiCapabilityProbeResult(
                Authentication: exception.Kind is not AiFailureKind.Authentication,
                ModelAvailable: exception.SafeErrorCode is not "gemini_model_not_found",
                ImageInput: false,
                StructuredOutput: false,
                UsageMetadata: false,
                State: "failed",
                SafeErrorCode: exception.SafeErrorCode,
                Latency: null);
        }
    }

    private static byte[] BuildRequestBody(AiProviderRequest request)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("system_instruction");
            writer.WriteStartObject();
            writer.WriteString("role", "system");
            writer.WritePropertyName("parts");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("text", request.SystemInstruction);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WritePropertyName("contents");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WritePropertyName("parts");
            writer.WriteStartArray();
            foreach (var media in request.Media)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("inline_data");
                writer.WriteStartObject();
                writer.WriteString("mime_type", media.MimeType);
                writer.WriteBase64String("data", media.Bytes.Span);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteStartObject();
            writer.WriteString("text", request.UserInstruction);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WritePropertyName("generationConfig");
            writer.WriteStartObject();
            writer.WriteString("responseMimeType", "application/json");
            writer.WritePropertyName("responseJsonSchema");
            request.ResponseJsonSchema.WriteTo(writer);
            writer.WriteNumber("maxOutputTokens", request.MaxOutputTokens);
            writer.WritePropertyName("thinkingConfig");
            writer.WriteStartObject();
            writer.WriteBoolean("includeThoughts", false);
            writer.WriteString("thinkingLevel", request.ThinkingLevel);
            writer.WriteEndObject();
            writer.WriteString("mediaResolution", request.MediaResolution);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        if (buffer.Length > MaximumInlineRequestBytes)
        {
            throw Failure(
                AiFailureKind.RequestRejected,
                "gemini_inline_request_too_large",
                isTransient: false);
        }

        return buffer.ToArray();
    }

    private static AiProviderResponse ParseResponse(
        AiConnectionSettings connection,
        ReadOnlyMemory<byte> responseBytes,
        TimeSpan latency)
    {
        try
        {
            using var envelope = JsonDocument.Parse(responseBytes);
            var root = envelope.RootElement;
            var responseId = GetString(root, "responseId");
            var actualModel = GetString(root, "modelVersion");
            if (!root.TryGetProperty("candidates", out var candidates)
                || candidates.ValueKind is not JsonValueKind.Array
                || candidates.GetArrayLength() != 1)
            {
                if (root.TryGetProperty("promptFeedback", out var feedback)
                    && GetString(feedback, "blockReason") is { Length: > 0 })
                {
                    throw Failure(
                        AiFailureKind.SafetyBlocked,
                        "gemini_prompt_blocked",
                        isTransient: false);
                }

                throw Failure(
                    AiFailureKind.InvalidResponse,
                    "gemini_candidate_missing",
                    isTransient: false);
            }

            var candidate = candidates[0];
            var finishReason = GetString(candidate, "finishReason") ?? "UNKNOWN";
            if (!string.Equals(finishReason, "STOP", StringComparison.Ordinal))
            {
                throw finishReason is "SAFETY" or "BLOCKLIST" or "PROHIBITED_CONTENT"
                    ? Failure(
                        AiFailureKind.SafetyBlocked,
                        "gemini_output_blocked",
                        isTransient: false)
                    : Failure(
                        AiFailureKind.InvalidResponse,
                        "gemini_finish_reason_invalid",
                        isTransient: false);
            }

            if (!candidate.TryGetProperty("content", out var content)
                || !content.TryGetProperty("parts", out var parts)
                || parts.ValueKind is not JsonValueKind.Array)
            {
                throw Failure(
                    AiFailureKind.InvalidResponse,
                    "gemini_content_missing",
                    isTransient: false);
            }

            var text = new StringBuilder();
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("thought", out var thought)
                    && thought.ValueKind is JsonValueKind.True)
                {
                    continue;
                }

                if (GetString(part, "text") is { } value)
                {
                    text.Append(value);
                }
            }

            if (text.Length is 0 or > MaximumResponseBytes)
            {
                throw Failure(
                    AiFailureKind.InvalidResponse,
                    "gemini_structured_output_missing",
                    isTransient: false);
            }

            using var structured = JsonDocument.Parse(text.ToString());
            var usage = root.TryGetProperty("usageMetadata", out var usageMetadata)
                ? new AiUsage(
                    GetInt32(usageMetadata, "promptTokenCount"),
                    GetInt32(usageMetadata, "cachedContentTokenCount"),
                    GetInt32(usageMetadata, "candidatesTokenCount"),
                    GetInt32(usageMetadata, "thoughtsTokenCount"),
                    GetInt32(usageMetadata, "totalTokenCount"))
                : new AiUsage(null, null, null, null, null);
            return new AiProviderResponse(
                AiProviders.GeminiDirect,
                connection.ModelId,
                actualModel,
                responseId,
                finishReason,
                structured.RootElement.Clone(),
                usage,
                latency);
        }
        catch (AiProviderException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new AiProviderException(
                AiFailureKind.InvalidResponse,
                "gemini_json_invalid",
                isTransient: false,
                innerException: exception);
        }
    }

    private static Uri BuildGenerateUri(AiConnectionSettings connection)
    {
        var escapedModel = Uri.EscapeDataString(connection.ModelId);
        return new Uri(
            connection.BaseAddress,
            $"v1beta/models/{escapedModel}:generateContent");
    }

    private static void ValidateConnection(AiConnectionSettings connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Provider != AiProviders.GeminiDirect
            || connection.BaseAddress.Scheme != Uri.UriSchemeHttps
            || !string.Equals(
                connection.BaseAddress.Host,
                AllowedHost,
                StringComparison.OrdinalIgnoreCase)
            || !ModelIdPattern().IsMatch(connection.ModelId)
            || connection.Timeout < TimeSpan.FromSeconds(5)
            || connection.Timeout > TimeSpan.FromMinutes(5))
        {
            throw Failure(
                AiFailureKind.InvalidConfiguration,
                "gemini_configuration_invalid",
                isTransient: false);
        }
    }

    private static void ValidateRequest(AiProviderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RequestKey)
            || request.RequestKey.Length > 200
            || string.IsNullOrWhiteSpace(request.TaskType)
            || request.SystemInstruction.Length is 0 or > 20_000
            || request.UserInstruction.Length is 0 or > 100_000
            || request.Media.Count is 0 or > 32
            || request.Media.Any(media =>
                media.Bytes.IsEmpty
                || media.Bytes.Length > MaximumInlineRequestBytes
                || media.MimeType is not (
                    "image/png"
                    or "image/jpeg"
                    or "image/webp"
                    or "application/pdf")
                || !Sha256Pattern().IsMatch(media.Sha256))
            || request.MaxOutputTokens is < 64 or > 65_536
            || request.ThinkingLevel is not (
                "MINIMAL"
                or "LOW"
                or "MEDIUM"
                or "HIGH")
            || request.ResponseJsonSchema.ValueKind is not JsonValueKind.Object)
        {
            throw Failure(
                AiFailureKind.RequestRejected,
                "ai_request_invalid",
                isTransient: false);
        }
    }

    private static async Task<AiProviderException> FromHttpStatusAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var retryAfter = response.Headers.RetryAfter?.Delta;
        var safeErrorCode = response.StatusCode is HttpStatusCode.BadRequest
            ? await ClassifyBadRequestAsync(
                    response.Content,
                    cancellationToken)
                .ConfigureAwait(false)
            : null;
        return response.StatusCode switch
        {
            HttpStatusCode.BadRequest => Failure(
                AiFailureKind.RequestRejected,
                safeErrorCode ?? "gemini_request_invalid",
                isTransient: false),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Failure(
                AiFailureKind.Authentication,
                "gemini_authentication_failed",
                isTransient: false),
            HttpStatusCode.NotFound => Failure(
                AiFailureKind.InvalidConfiguration,
                "gemini_model_not_found",
                isTransient: false),
            HttpStatusCode.PaymentRequired => Failure(
                AiFailureKind.BudgetBlocked,
                "gemini_billing_required",
                isTransient: false),
            HttpStatusCode.TooManyRequests => new AiProviderException(
                AiFailureKind.RateLimited,
                "gemini_rate_limited",
                isTransient: true,
                retryAfter),
            HttpStatusCode.InternalServerError
                or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout => new AiProviderException(
                    AiFailureKind.TransientProvider,
                    "gemini_provider_unavailable",
                    isTransient: true,
                    retryAfter),
            _ => Failure(
                AiFailureKind.RequestRejected,
                $"gemini_http_{((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)}",
                isTransient: false),
        };
    }

    private static async Task<string?> ClassifyBadRequestAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        try
        {
            if (content.Headers.ContentLength > MaximumErrorResponseBytes)
            {
                return null;
            }

            await using var source = await content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var destination = new MemoryStream();
            var buffer = ArrayPool<byte>.Shared.Rent(4 * 1024);
            try
            {
                while (true)
                {
                    var remaining = MaximumErrorResponseBytes - (int)destination.Length;
                    if (remaining == 0)
                    {
                        return null;
                    }

                    var read = await source.ReadAsync(
                            buffer.AsMemory(0, Math.Min(buffer.Length, remaining)),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await destination.WriteAsync(
                            buffer.AsMemory(0, read),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }

            using var envelope = JsonDocument.Parse(destination.GetBuffer().AsMemory(
                0,
                checked((int)destination.Length)));
            if (!envelope.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind is not JsonValueKind.Object)
            {
                return null;
            }

            if (error.TryGetProperty("details", out var details)
                && details.ValueKind is JsonValueKind.Array)
            {
                foreach (var detail in details.EnumerateArray())
                {
                    if (detail.ValueKind is not JsonValueKind.Object
                        || !detail.TryGetProperty(
                            "fieldViolations",
                            out var violations)
                        || violations.ValueKind is not JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var violation in violations.EnumerateArray())
                    {
                        if (violation.ValueKind is JsonValueKind.Object
                            && GetString(violation, "field") is { } field
                            && ClassifyBadRequestEvidence(field) is { } classified)
                        {
                            return classified;
                        }
                    }
                }
            }

            return GetString(error, "message") is { } message
                ? ClassifyBadRequestEvidence(message)
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException
                or HttpRequestException
                or InvalidOperationException
                or JsonException
                or NotSupportedException)
        {
            return null;
        }
    }

    private static string? ClassifyBadRequestEvidence(string evidence)
    {
        if (ContainsAny(
                evidence,
                "responseJsonSchema",
                "response_json_schema",
                "responseSchema",
                "response_schema"))
        {
            return "gemini_response_schema_invalid";
        }

        if (ContainsAny(
                evidence,
                "mediaResolution",
                "media_resolution"))
        {
            return "gemini_media_resolution_invalid";
        }

        if (ContainsAny(
                evidence,
                "thinkingConfig",
                "thinking_config",
                "thinkingLevel",
                "thinking_level"))
        {
            return "gemini_thinking_config_invalid";
        }

        if (ContainsAny(
                evidence,
                "maxOutputTokens",
                "max_output_tokens"))
        {
            return "gemini_output_limit_invalid";
        }

        if (ContainsAny(
                evidence,
                "inlineData",
                "inline_data",
                "mimeType",
                "mime_type"))
        {
            return "gemini_media_invalid";
        }

        return null;
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate =>
            value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes)
        {
            throw Failure(
                AiFailureKind.InvalidResponse,
                "gemini_response_too_large",
                isTransient: false);
        }

        await using var source = await content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(
                        buffer.AsMemory(0, buffer.Length),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (destination.Length + read > maximumBytes)
                {
                    throw Failure(
                        AiFailureKind.InvalidResponse,
                        "gemini_response_too_large",
                        isTransient: false);
                }

                await destination.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return destination.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static string? GetString(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out var property)
        && property.ValueKind is JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? GetInt32(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out var property)
        && property.TryGetInt32(out var result)
            ? result
            : null;

    private static AiProviderException Failure(
        AiFailureKind kind,
        string safeErrorCode,
        bool isTransient) =>
        new(kind, safeErrorCode, isTransient);

    [GeneratedRegex(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ModelIdPattern();

    [GeneratedRegex(
        "^[0-9a-f]{64}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}
