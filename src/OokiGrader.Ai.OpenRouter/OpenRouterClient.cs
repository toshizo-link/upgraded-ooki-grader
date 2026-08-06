using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OokiGrader.Ai.Abstractions;

namespace OokiGrader.Ai.OpenRouter;

/// <summary>
/// Non-streaming OpenRouter Chat Completions adapter. Requests contain only
/// inline private media, the approved model identifier, and a strict JSON
/// schema; no endpoint or credential is accepted from model-visible content.
/// </summary>
public sealed partial class OpenRouterClient(HttpClient httpClient)
    : IAiProviderClient
{
    private const int MaximumInlineMediaBytes = 18 * 1024 * 1024;
    private const int MaximumRequestBytes = 25 * 1024 * 1024;
    private const int MaximumResponseBytes = 8 * 1024 * 1024;
    private const int MaximumErrorResponseBytes = 64 * 1024;
    private static readonly byte[] ProbePng =
    [
        137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82,
        0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0, 31, 21, 196, 137,
        0, 0, 0, 13, 73, 68, 65, 84, 8, 215, 99, 248, 255, 255, 255,
        127, 0, 9, 251, 3, 253, 42, 134, 227, 138, 0, 0, 0, 0, 73,
        69, 78, 68, 174, 66, 96, 130,
    ];

    public string Provider => AiProviders.OpenRouter;

    public async Task<AiProviderResponse> GenerateAsync(
        AiConnectionSettings connection,
        ReadOnlyMemory<byte> credentialUtf8,
        AiProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateConnection(connection);
        ValidateRequest(request);
        ValidateCredential(credentialUtf8);

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        linkedCancellation.CancelAfter(connection.Timeout);
        var started = Stopwatch.GetTimestamp();

        try
        {
            using var message = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(connection.BaseAddress, "chat/completions"));
            message.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                Encoding.ASCII.GetString(credentialUtf8.Span));
            message.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            message.Content = new ByteArrayContent(BuildRequestBody(
                connection.ModelId,
                request));
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
                    "openrouter_response_too_large",
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
                "openrouter_timeout",
                isTransient: true,
                innerException: exception);
        }
        catch (HttpRequestException exception)
        {
            throw new AiProviderException(
                AiFailureKind.TransientProvider,
                "openrouter_network_error",
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
        var textProbe = new AiProviderRequest(
            $"probe_{Guid.NewGuid():N}",
            "capabilityProbe",
            "capability-probe-v1",
            "capability-probe-v1",
            "Output only the requested schema.",
            "Return {\"ok\":true}.",
            schema.RootElement.Clone(),
            [],
            MaxOutputTokens: 64);

        AiProviderResponse textResponse;
        try
        {
            textResponse = await GenerateAsync(
                    connection,
                    credentialUtf8,
                    textProbe,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AiProviderException exception)
        {
            return FailedProbe(exception);
        }

        var textAccepted = textResponse.StructuredOutput.TryGetProperty(
                    "ok",
                    out var ok)
                && ok.ValueKind is JsonValueKind.True;
        var usageAvailable = textResponse.Usage.TotalTokens is not null
            && textResponse.Usage.ProviderCostUsdMicros is not null;
        if (!textAccepted)
        {
            return new AiCapabilityProbeResult(
                Authentication: true,
                ModelAvailable: true,
                ImageInput: false,
                StructuredOutput: false,
                UsageMetadata: usageAvailable,
                State: "failed",
                SafeErrorCode: "openrouter_probe_value_invalid",
                Latency: textResponse.Latency);
        }

        if (!usageAvailable)
        {
            return new AiCapabilityProbeResult(
                Authentication: true,
                ModelAvailable: true,
                ImageInput: false,
                StructuredOutput: true,
                UsageMetadata: false,
                State: "failed",
                SafeErrorCode: "openrouter_usage_metadata_missing",
                Latency: textResponse.Latency);
        }

        var imageProbe = textProbe with
        {
            RequestKey = $"probe_image_{Guid.NewGuid():N}",
            SystemInstruction =
                "The image is synthetic test data. Output only the requested schema.",
            UserInstruction =
                "Inspect the one-pixel image and return {\"ok\":true}.",
            Media =
            [
                new AiMediaPart(
                    "image/png",
                    ProbePng,
                    Convert.ToHexString(
                            System.Security.Cryptography.SHA256.HashData(ProbePng))
                        .ToLowerInvariant()),
            ],
            MediaResolution = "MEDIA_RESOLUTION_LOW",
        };
        try
        {
            var imageResponse = await GenerateAsync(
                    connection,
                    credentialUtf8,
                    imageProbe,
                    cancellationToken)
                .ConfigureAwait(false);
            var imageAccepted = imageResponse.StructuredOutput.TryGetProperty(
                    "ok",
                    out var imageOk)
                && imageOk.ValueKind is JsonValueKind.True;
            return new AiCapabilityProbeResult(
                Authentication: true,
                ModelAvailable: true,
                ImageInput: imageAccepted,
                StructuredOutput: true,
                UsageMetadata: usageAvailable,
                State: imageAccepted ? "passed" : "failed",
                SafeErrorCode: imageAccepted
                    ? null
                    : "openrouter_image_probe_value_invalid",
                Latency: textResponse.Latency + imageResponse.Latency);
        }
        catch (AiProviderException exception)
        {
            return new AiCapabilityProbeResult(
                Authentication: true,
                ModelAvailable: true,
                ImageInput: false,
                StructuredOutput: true,
                UsageMetadata: usageAvailable,
                State: "failed",
                SafeErrorCode: exception.SafeErrorCode,
                Latency: textResponse.Latency);
        }
    }

    private static AiCapabilityProbeResult FailedProbe(
        AiProviderException exception) =>
        new(
            Authentication: exception.Kind is not AiFailureKind.Authentication,
            ModelAvailable: exception.SafeErrorCode is not
                "openrouter_model_not_found",
            ImageInput: false,
            StructuredOutput: false,
            UsageMetadata: false,
            State: "failed",
            SafeErrorCode: exception.SafeErrorCode,
            Latency: null);

    private static byte[] BuildRequestBody(
        string modelId,
        AiProviderRequest request)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", modelId);
            writer.WriteBoolean("stream", false);
            writer.WriteNumber("max_tokens", request.MaxOutputTokens);

            writer.WritePropertyName("reasoning");
            writer.WriteStartObject();
            writer.WriteString("effort", ToReasoningEffort(request.ThinkingLevel));
            writer.WriteBoolean("exclude", true);
            writer.WriteEndObject();

            writer.WritePropertyName("messages");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("role", "system");
            writer.WriteString("content", request.SystemInstruction);
            writer.WriteEndObject();
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", request.UserInstruction);
            writer.WriteEndObject();
            for (var index = 0; index < request.Media.Count; index++)
            {
                WriteMedia(writer, request.Media[index], index);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WritePropertyName("response_format");
            writer.WriteStartObject();
            writer.WriteString("type", "json_schema");
            writer.WritePropertyName("json_schema");
            writer.WriteStartObject();
            writer.WriteString("name", "ooki_grader_response");
            writer.WriteBoolean("strict", true);
            writer.WritePropertyName("schema");
            WriteStrictSchema(writer, request.ResponseJsonSchema);
            writer.WriteEndObject();
            writer.WriteEndObject();

            writer.WritePropertyName("provider");
            writer.WriteStartObject();
            writer.WriteBoolean("require_parameters", true);
            writer.WriteString("data_collection", "deny");
            writer.WriteBoolean("zdr", true);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        if (buffer.Length > MaximumRequestBytes)
        {
            throw Failure(
                AiFailureKind.RequestRejected,
                "openrouter_inline_request_too_large",
                isTransient: false);
        }

        return buffer.ToArray();
    }

    private static void WriteStrictSchema(
        Utf8JsonWriter writer,
        JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray())
            {
                WriteStrictSchema(writer, item);
            }

            writer.WriteEndArray();
            return;
        }

        if (element.ValueKind is not JsonValueKind.Object)
        {
            element.WriteTo(writer);
            return;
        }

        var isObjectSchema = element.TryGetProperty("properties", out _)
            || (element.TryGetProperty("type", out var type)
                && (type.ValueKind is JsonValueKind.String
                        && type.GetString() == "object"
                    || type.ValueKind is JsonValueKind.Array
                        && type.EnumerateArray().Any(item =>
                            item.ValueKind is JsonValueKind.String
                            && item.GetString() == "object")));
        var hasAdditionalProperties = false;
        writer.WriteStartObject();
        foreach (var property in element.EnumerateObject())
        {
            hasAdditionalProperties |= property.NameEquals(
                "additionalProperties");
            writer.WritePropertyName(property.Name);
            WriteStrictSchema(writer, property.Value);
        }

        if (isObjectSchema && !hasAdditionalProperties)
        {
            writer.WriteBoolean("additionalProperties", false);
        }

        writer.WriteEndObject();
    }

    private static void WriteMedia(
        Utf8JsonWriter writer,
        AiMediaPart media,
        int index)
    {
        writer.WriteStartObject();
        if (media.MimeType == "application/pdf")
        {
            writer.WriteString("type", "file");
            writer.WritePropertyName("file");
            writer.WriteStartObject();
            writer.WriteString(
                "filename",
                $"document-{index + 1}-{media.Sha256[..12]}.pdf");
            writer.WritePropertyName("file_data");
            WriteDataUri(writer, media);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteString("type", "image_url");
            writer.WritePropertyName("image_url");
            writer.WriteStartObject();
            writer.WritePropertyName("url");
            WriteDataUri(writer, media);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteDataUri(Utf8JsonWriter writer, AiMediaPart media)
    {
        writer.WriteStringValue(
            $"data:{media.MimeType};base64,{Convert.ToBase64String(media.Bytes.Span)}");
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
            if (TryGetError(root, out var topLevelError))
            {
                throw FromEmbeddedError(topLevelError);
            }

            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind is not JsonValueKind.Array
                || choices.GetArrayLength() != 1)
            {
                throw Failure(
                    AiFailureKind.InvalidResponse,
                    "openrouter_choice_missing",
                    isTransient: false);
            }

            var choice = choices[0];
            if (TryGetError(choice, out var choiceError))
            {
                throw FromEmbeddedError(choiceError);
            }

            var finishReason = GetString(choice, "finish_reason") ?? "unknown";
            if (finishReason != "stop")
            {
                throw finishReason is "content_filter" or "safety"
                    ? Failure(
                        AiFailureKind.SafetyBlocked,
                        "openrouter_output_blocked",
                        isTransient: false)
                    : finishReason == "error"
                        ? Failure(
                            AiFailureKind.TransientProvider,
                            "openrouter_completion_error",
                            isTransient: true)
                        : Failure(
                            AiFailureKind.InvalidResponse,
                            "openrouter_finish_reason_invalid",
                            isTransient: false);
            }

            if (!choice.TryGetProperty("message", out var assistantMessage)
                || assistantMessage.ValueKind is not JsonValueKind.Object)
            {
                throw Failure(
                    AiFailureKind.InvalidResponse,
                    "openrouter_message_missing",
                    isTransient: false);
            }

            if (GetString(assistantMessage, "refusal") is { Length: > 0 })
            {
                throw Failure(
                    AiFailureKind.SafetyBlocked,
                    "openrouter_output_blocked",
                    isTransient: false);
            }

            var content = GetString(assistantMessage, "content");
            if (string.IsNullOrWhiteSpace(content)
                || Encoding.UTF8.GetByteCount(content) > MaximumResponseBytes)
            {
                throw Failure(
                    AiFailureKind.InvalidResponse,
                    "openrouter_structured_output_missing",
                    isTransient: false);
            }

            using var structured = JsonDocument.Parse(content);
            if (structured.RootElement.ValueKind is not JsonValueKind.Object)
            {
                throw Failure(
                    AiFailureKind.InvalidResponse,
                    "openrouter_structured_output_invalid",
                    isTransient: false);
            }

            var usage = root.TryGetProperty("usage", out var usageElement)
                && usageElement.ValueKind is JsonValueKind.Object
                    ? ParseUsage(usageElement)
                    : new AiUsage(null, null, null, null, null);
            var actualModel = GetBoundedString(root, "model", 128);
            var responseId = GetBoundedString(root, "id", 500);
            var routedProvider = GetOptionalSafeString(root, "provider", 64);
            if (actualModel is null
                || actualModel != connection.ModelId
                || responseId is null)
            {
                throw Failure(
                    AiFailureKind.InvalidResponse,
                    "openrouter_response_metadata_invalid",
                    isTransient: false);
            }

            return new AiProviderResponse(
                AiProviders.OpenRouter,
                connection.ModelId,
                actualModel,
                responseId,
                finishReason,
                structured.RootElement.Clone(),
                usage,
                latency,
                routedProvider);
        }
        catch (AiProviderException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new AiProviderException(
                AiFailureKind.InvalidResponse,
                "openrouter_json_invalid",
                isTransient: false,
                innerException: exception);
        }
    }

    private static AiUsage ParseUsage(JsonElement usage)
    {
        var promptTokens = GetNonNegativeInt32(usage, "prompt_tokens");
        var completionTokens = GetNonNegativeInt32(usage, "completion_tokens");
        var totalTokens = GetNonNegativeInt32(usage, "total_tokens");
        int? cachedTokens = null;
        if (usage.TryGetProperty("prompt_tokens_details", out var promptDetails)
            && promptDetails.ValueKind is JsonValueKind.Object)
        {
            cachedTokens = GetNonNegativeInt32(promptDetails, "cached_tokens");
        }

        int? thinkingTokens = null;
        if (usage.TryGetProperty(
                "completion_tokens_details",
                out var completionDetails)
            && completionDetails.ValueKind is JsonValueKind.Object)
        {
            thinkingTokens = GetNonNegativeInt32(
                completionDetails,
                "reasoning_tokens");
        }

        if (thinkingTokens is not null
            && (completionTokens is null
                || thinkingTokens > completionTokens))
        {
            throw Failure(
                AiFailureKind.InvalidResponse,
                "openrouter_usage_invalid",
                isTransient: false);
        }

        // OpenRouter follows OpenAI usage semantics: reasoning tokens are a
        // subset of completion_tokens. Normalize them into disjoint fields so
        // downstream pricing cannot charge the same tokens twice.
        var outputTokens = completionTokens - (thinkingTokens ?? 0);
        var providerCostUsdMicros = GetProviderCostUsdMicros(usage);
        return new AiUsage(
            promptTokens,
            cachedTokens,
            outputTokens,
            thinkingTokens,
            totalTokens,
            providerCostUsdMicros);
    }

    private static void ValidateConnection(AiConnectionSettings connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var address = connection.BaseAddress;
        if (connection.Provider != AiProviders.OpenRouter
            || address.Scheme != Uri.UriSchemeHttps
            || !string.Equals(
                address.Host,
                "openrouter.ai",
                StringComparison.OrdinalIgnoreCase)
            || !address.IsDefaultPort
            || address.AbsolutePath != "/api/v1/"
            || address.Query.Length != 0
            || address.Fragment.Length != 0
            || address.UserInfo.Length != 0
            || !AiProviderCatalog.IsModelIdValid(
                AiProviders.OpenRouter,
                connection.ModelId)
            || connection.Timeout < TimeSpan.FromSeconds(5)
            || connection.Timeout > TimeSpan.FromMinutes(5))
        {
            throw Failure(
                AiFailureKind.InvalidConfiguration,
                "openrouter_configuration_invalid",
                isTransient: false);
        }

    }

    private static void ValidateCredential(ReadOnlyMemory<byte> credentialUtf8)
    {
        if (credentialUtf8.Length is < 20 or > 512
            || credentialUtf8.Span.IndexOfAnyExceptInRange(
                (byte)0x21,
                (byte)0x7e) >= 0)
        {
            throw Failure(
                AiFailureKind.Authentication,
                "openrouter_credential_invalid",
                isTransient: false);
        }
    }

    private static void ValidateRequest(AiProviderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var totalMediaBytes = 0L;
        foreach (var media in request.Media)
        {
            totalMediaBytes = checked(totalMediaBytes + media.Bytes.Length);
        }

        if (string.IsNullOrWhiteSpace(request.RequestKey)
            || request.RequestKey.Length > 200
            || string.IsNullOrWhiteSpace(request.TaskType)
            || request.SystemInstruction.Length is 0 or > 20_000
            || request.UserInstruction.Length is 0 or > 100_000
            || request.Media.Count > 32
            || totalMediaBytes > MaximumInlineMediaBytes
            || request.Media.Any(media =>
                media.Bytes.IsEmpty
                || media.Bytes.Length > MaximumInlineMediaBytes
                || media.MimeType is not (
                    "image/png"
                    or "image/jpeg"
                    or "image/webp"
                    or "application/pdf")
                || !Sha256Pattern().IsMatch(media.Sha256))
            || request.MaxOutputTokens is < 64 or > 65_536
            || !IsThinkingLevelValid(request.ThinkingLevel)
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
        var error = await ReadErrorAsync(
                response.Content,
                cancellationToken)
            .ConfigureAwait(false);
        if (error.ImageNotSupported)
        {
            return Failure(
                AiFailureKind.InvalidConfiguration,
                "openrouter_image_not_supported",
                isTransient: false);
        }

        return FromErrorCode(
            error.Code ?? (int)response.StatusCode,
            retryAfter);
    }

    private static AiProviderException FromEmbeddedError(JsonElement error)
    {
        if (IsImageNotSupported(error))
        {
            return Failure(
                AiFailureKind.InvalidConfiguration,
                "openrouter_image_not_supported",
                isTransient: false);
        }

        var code = error.TryGetProperty("code", out var codeElement)
            && codeElement.TryGetInt32(out var parsed)
                ? parsed
                : 502;
        return FromErrorCode(code, retryAfter: null);
    }

    private static AiProviderException FromErrorCode(
        int code,
        TimeSpan? retryAfter)
    {
        return code switch
        {
            400 => Failure(
                AiFailureKind.RequestRejected,
                "openrouter_request_invalid",
                isTransient: false),
            401 => Failure(
                AiFailureKind.Authentication,
                "openrouter_authentication_failed",
                isTransient: false),
            402 => Failure(
                AiFailureKind.BudgetBlocked,
                "openrouter_credits_required",
                isTransient: false),
            403 => Failure(
                AiFailureKind.SafetyBlocked,
                "openrouter_request_blocked",
                isTransient: false),
            404 => Failure(
                AiFailureKind.InvalidConfiguration,
                "openrouter_model_not_found",
                isTransient: false),
            408 or 524 => new AiProviderException(
                AiFailureKind.Timeout,
                "openrouter_provider_timeout",
                isTransient: true,
                retryAfter),
            413 => Failure(
                AiFailureKind.RequestRejected,
                "openrouter_request_too_large",
                isTransient: false),
            429 => new AiProviderException(
                AiFailureKind.RateLimited,
                "openrouter_rate_limited",
                isTransient: true,
                retryAfter),
            >= 500 and <= 599 => new AiProviderException(
                AiFailureKind.TransientProvider,
                "openrouter_provider_unavailable",
                isTransient: true,
                retryAfter),
            _ => Failure(
                AiFailureKind.RequestRejected,
                $"openrouter_http_{code.ToString(CultureInfo.InvariantCulture)}",
                isTransient: false),
        };
    }

    private static async Task<ParsedProviderError> ReadErrorAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await ReadBoundedAsync(
                    content,
                    MaximumErrorResponseBytes,
                    "openrouter_error_response_too_large",
                    cancellationToken)
                .ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind is not JsonValueKind.Object)
            {
                return default;
            }

            var code = error.TryGetProperty("code", out var codeElement)
                && codeElement.TryGetInt32(out var parsed)
                    ? parsed
                    : (int?)null;
            return new ParsedProviderError(code, IsImageNotSupported(error));
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
                or NotSupportedException
                or AiProviderException)
        {
            return default;
        }
    }

    private static bool IsImageNotSupported(JsonElement error)
    {
        var message = GetString(error, "message")?.Trim();
        return message is not null
            && string.Equals(
                message.TrimEnd('.'),
                "No endpoints found that support image input",
                StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        string tooLargeErrorCode,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes)
        {
            throw Failure(
                AiFailureKind.InvalidResponse,
                tooLargeErrorCode,
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
                        tooLargeErrorCode,
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

    private static bool TryGetError(
        JsonElement value,
        out JsonElement error)
    {
        if (value.TryGetProperty("error", out error)
            && error.ValueKind is JsonValueKind.Object)
        {
            return true;
        }

        error = default;
        return false;
    }

    private static string? GetString(
        JsonElement value,
        string propertyName) =>
        value.TryGetProperty(propertyName, out var property)
        && property.ValueKind is JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? GetBoundedString(
        JsonElement value,
        string propertyName,
        int maximumCharacters)
    {
        var result = GetString(value, propertyName);
        return result is { Length: > 0 }
            && result.Length <= maximumCharacters
                ? result
                : null;
    }

    private static string? GetOptionalSafeString(
        JsonElement value,
        string propertyName,
        int maximumCharacters)
    {
        if (!value.TryGetProperty(propertyName, out var property)
            || property.ValueKind is JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind is not JsonValueKind.String
            || property.GetString() is not { } result
            || string.IsNullOrWhiteSpace(result)
            || result.Length > maximumCharacters
            || result != result.Trim()
            || result.Any(char.IsControl))
        {
            throw Failure(
                AiFailureKind.InvalidResponse,
                "openrouter_response_metadata_invalid",
                isTransient: false);
        }

        return result;
    }

    private static int? GetNonNegativeInt32(
        JsonElement value,
        string propertyName) =>
        value.TryGetProperty(propertyName, out var property)
        && property.TryGetInt32(out var result)
        && result >= 0
            ? result
            : null;

    private static long? GetProviderCostUsdMicros(JsonElement usage)
    {
        if (!usage.TryGetProperty("cost", out var property)
            || property.ValueKind is JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind is not JsonValueKind.Number
            || !property.TryGetDecimal(out var costUsd)
            || costUsd < 0)
        {
            throw Failure(
                AiFailureKind.InvalidResponse,
                "openrouter_usage_invalid",
                isTransient: false);
        }

        try
        {
            var micros = decimal.Ceiling(costUsd * 1_000_000m);
            if (micros > long.MaxValue)
            {
                throw new OverflowException();
            }

            return decimal.ToInt64(micros);
        }
        catch (OverflowException)
        {
            throw Failure(
                AiFailureKind.InvalidResponse,
                "openrouter_usage_invalid",
                isTransient: false);
        }
    }

    private static bool IsThinkingLevelValid(string thinkingLevel) =>
        thinkingLevel is "MINIMAL" or "LOW" or "MEDIUM" or "HIGH";

    private static string ToReasoningEffort(string thinkingLevel) =>
        thinkingLevel switch
        {
            "MINIMAL" => "minimal",
            "LOW" => "low",
            "MEDIUM" => "medium",
            "HIGH" => "high",
            _ => throw Failure(
                AiFailureKind.RequestRejected,
                "ai_request_invalid",
                isTransient: false),
        };

    private static AiProviderException Failure(
        AiFailureKind kind,
        string safeErrorCode,
        bool isTransient) =>
        new(kind, safeErrorCode, isTransient);

    private readonly record struct ParsedProviderError(
        int? Code,
        bool ImageNotSupported);

    [GeneratedRegex(
        "^[0-9a-f]{64}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}
