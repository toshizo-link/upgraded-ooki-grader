using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OokiGrader.Ai.Abstractions;

namespace OokiGrader.Ai.Gemini;

/// <summary>
/// REST adapter for the Gemini Developer API generateContent Batch API.
/// The adapter intentionally exposes no create retry. A transport failure after
/// SendAsync begins is classified as ambiguous so the durable caller can adopt a
/// matching remote batch instead of risking duplicate work and billing.
/// </summary>
public sealed partial class GeminiBatchClient(HttpClient httpClient)
    : IAiBatchProviderClient
{
    public const string SelectedModel = "gemini-3.5-flash-lite";

    private const string AllowedHost = "generativelanguage.googleapis.com";
    private const int MaximumJsonLinesBytes = 1024 * 1024 * 1024;
    private const int MaximumControlResponseBytes = 8 * 1024 * 1024;
    private const int MaximumResultsBytes = 256 * 1024 * 1024;

    public string Provider => AiProviders.GeminiDirect;

    public byte[] BuildJsonLines(IReadOnlyList<AiProviderRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count is < 1 or > 10_000
            || requests.Any(item => item is null)
            || requests.Select(item => item.RequestKey)
                .Distinct(StringComparer.Ordinal)
                .Count() != requests.Count)
        {
            throw Failure(
                AiFailureKind.RequestRejected,
                "gemini_batch_requests_invalid",
                isTransient: false);
        }

        using var destination = new MemoryStream();
        foreach (var request in requests)
        {
            ValidateProviderRequest(request);
            using var line = new MemoryStream();
            using (var writer = new Utf8JsonWriter(line))
            {
                writer.WriteStartObject();
                writer.WriteString("key", request.RequestKey);
                writer.WritePropertyName("request");
                WriteGenerateContentRequest(writer, request);
                writer.WriteEndObject();
            }

            if (destination.Length + line.Length + 1 > MaximumJsonLinesBytes)
            {
                throw Failure(
                    AiFailureKind.RequestRejected,
                    "gemini_batch_jsonl_too_large",
                    isTransient: false);
            }

            line.Position = 0;
            line.CopyTo(destination);
            destination.WriteByte((byte)'\n');
        }

        return destination.ToArray();
    }

    public async Task<AiBatchInputFile> UploadJsonLinesAsync(
        AiConnectionSettings connection,
        ReadOnlyMemory<byte> credentialUtf8,
        string displayName,
        ReadOnlyMemory<byte> jsonLines,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionAndCredential(connection, credentialUtf8);
        if (!DisplayNamePattern().IsMatch(displayName)
            || jsonLines.IsEmpty
            || jsonLines.Length > MaximumJsonLinesBytes)
        {
            throw Failure(
                AiFailureKind.RequestRejected,
                "gemini_batch_upload_invalid",
                isTransient: false);
        }

        using var timeout = CreateTimeout(connection, cancellationToken);
        var credential = Encoding.UTF8.GetString(credentialUtf8.Span);
        using var start = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(connection.BaseAddress, "upload/v1beta/files"));
        AddCredential(start, credential);
        start.Headers.TryAddWithoutValidation(
            "X-Goog-Upload-Protocol",
            "resumable");
        start.Headers.TryAddWithoutValidation(
            "X-Goog-Upload-Command",
            "start");
        start.Headers.TryAddWithoutValidation(
            "X-Goog-Upload-Header-Content-Length",
            jsonLines.Length.ToString(CultureInfo.InvariantCulture));
        start.Headers.TryAddWithoutValidation(
            "X-Goog-Upload-Header-Content-Type",
            "application/jsonl");
        start.Content = JsonContent(
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                file = new
                {
                    display_name = displayName,
                },
            }));

        using var startResponse = await SendControlAsync(
                start,
                timeout.Token)
            .ConfigureAwait(false);
        if (!startResponse.IsSuccessStatusCode)
        {
            throw FromHttpStatus(startResponse);
        }

        if (!startResponse.Headers.TryGetValues(
                "X-Goog-Upload-URL",
                out var uploadValues)
            || !Uri.TryCreate(
                uploadValues.SingleOrDefault(),
                UriKind.Absolute,
                out var uploadUri)
            || uploadUri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(
                uploadUri.Host,
                AllowedHost,
                StringComparison.OrdinalIgnoreCase))
        {
            throw Failure(
                AiFailureKind.InvalidResponse,
                "gemini_batch_upload_url_invalid",
                isTransient: false);
        }

        using var upload = new HttpRequestMessage(HttpMethod.Post, uploadUri);
        AddCredential(upload, credential);
        upload.Headers.TryAddWithoutValidation("X-Goog-Upload-Offset", "0");
        upload.Headers.TryAddWithoutValidation(
            "X-Goog-Upload-Command",
            "upload, finalize");
        upload.Content = new ByteArrayContent(jsonLines.ToArray());
        upload.Content.Headers.ContentLength = jsonLines.Length;
        upload.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/jsonl");

        using var uploadResponse = await SendControlAsync(
                upload,
                timeout.Token)
            .ConfigureAwait(false);
        if (!uploadResponse.IsSuccessStatusCode)
        {
            throw FromHttpStatus(uploadResponse);
        }

        var bytes = await ReadBoundedAsync(
                uploadResponse.Content,
                MaximumControlResponseBytes,
                timeout.Token)
            .ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var file = document.RootElement.TryGetProperty("file", out var nested)
                ? nested
                : document.RootElement;
            var name = GetString(file, "name");
            if (!IsFileName(name))
            {
                throw Failure(
                    AiFailureKind.InvalidResponse,
                    "gemini_batch_upload_response_invalid",
                    isTransient: false);
            }

            return new AiBatchInputFile(
                name!,
                GetString(file, "uri"),
                GetDateTimeOffset(file, "expirationTime"),
                jsonLines.Length);
        }
        catch (JsonException exception)
        {
            throw new AiProviderException(
                AiFailureKind.InvalidResponse,
                "gemini_batch_upload_json_invalid",
                isTransient: false,
                innerException: exception);
        }
    }

    public async Task<AiBatchCreateReceipt> CreateAsync(
        AiConnectionSettings connection,
        ReadOnlyMemory<byte> credentialUtf8,
        AiBatchCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateConnectionAndCredential(connection, credentialUtf8);
            ArgumentNullException.ThrowIfNull(request);
            if (!DisplayNamePattern().IsMatch(request.DisplayName)
                || !Sha256Pattern().IsMatch(request.ManifestHash)
                || !IsFileName(request.InputFileName)
                || request.RequestCount is < 1 or > 10_000)
            {
                throw new AiBatchCreateException(
                    AiBatchCreateFailureKind.DefinitePreSend,
                    "gemini_batch_create_invalid",
                    isTransient: false);
            }
        }
        catch (AiProviderException exception)
        {
            throw new AiBatchCreateException(
                AiBatchCreateFailureKind.DefinitePreSend,
                exception.SafeErrorCode,
                exception.IsTransient,
                exception.RetryAfter,
                exception);
        }

        using var timeout = CreateTimeout(connection, cancellationToken);
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(
                connection.BaseAddress,
                $"v1beta/models/{Uri.EscapeDataString(connection.ModelId)}" +
                ":batchGenerateContent"));
        AddCredential(
            message,
            Encoding.UTF8.GetString(credentialUtf8.Span));
        message.Content = JsonContent(
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                batch = new
                {
                    display_name = request.DisplayName,
                    input_config = new
                    {
                        file_name = request.InputFileName,
                    },
                },
            }));

        try
        {
            using var response = await httpClient.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var failure = FromHttpStatus(response);
                throw new AiBatchCreateException(
                    AiBatchCreateFailureKind.DefiniteRemoteRejection,
                    failure.SafeErrorCode,
                    failure.IsTransient,
                    failure.RetryAfter,
                    failure);
            }

            var bytes = await ReadBoundedAsync(
                    response.Content,
                    MaximumControlResponseBytes,
                    timeout.Token)
                .ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var name = GetString(root, "name");
            var metadata = GetObject(root, "metadata");
            if (!IsBatchName(name))
            {
                throw new AiBatchCreateException(
                    AiBatchCreateFailureKind.AmbiguousAfterSend,
                    "gemini_batch_create_response_invalid",
                    isTransient: false);
            }

            return new AiBatchCreateReceipt(
                name!,
                GetString(metadata, "displayName")
                    ?? GetString(metadata, "display_name")
                    ?? request.DisplayName,
                GetDateTimeOffset(metadata, "createTime")
                    ?? GetDateTimeOffset(metadata, "create_time"));
        }
        catch (AiBatchCreateException)
        {
            throw;
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiBatchCreateException(
                AiBatchCreateFailureKind.AmbiguousAfterSend,
                "gemini_batch_create_timeout",
                isTransient: true,
                innerException: exception);
        }
        catch (HttpRequestException exception)
        {
            throw new AiBatchCreateException(
                AiBatchCreateFailureKind.AmbiguousAfterSend,
                "gemini_batch_create_network_unknown",
                isTransient: true,
                innerException: exception);
        }
        catch (JsonException exception)
        {
            throw new AiBatchCreateException(
                AiBatchCreateFailureKind.AmbiguousAfterSend,
                "gemini_batch_create_json_invalid",
                isTransient: false,
                innerException: exception);
        }
    }

    public async Task<AiBatchStatus> GetAsync(
        AiConnectionSettings connection,
        ReadOnlyMemory<byte> credentialUtf8,
        string providerBatchName,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionAndCredential(connection, credentialUtf8);
        if (!IsBatchName(providerBatchName))
        {
            throw Failure(
                AiFailureKind.RequestRejected,
                "gemini_batch_name_invalid",
                isTransient: false);
        }

        using var timeout = CreateTimeout(connection, cancellationToken);
        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(connection.BaseAddress, $"v1beta/{providerBatchName}"));
        AddCredential(
            message,
            Encoding.UTF8.GetString(credentialUtf8.Span));
        using var response = await SendControlAsync(message, timeout.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw FromHttpStatus(response);
        }

        var bytes = await ReadBoundedAsync(
                response.Content,
                MaximumControlResponseBytes,
                timeout.Token)
            .ConfigureAwait(false);
        return ParseStatus(bytes);
    }

    public async Task<AiBatchListPage> ListAsync(
        AiConnectionSettings connection,
        ReadOnlyMemory<byte> credentialUtf8,
        string? pageToken = null,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionAndCredential(connection, credentialUtf8);
        if (pageToken?.Length > 2_000)
        {
            throw Failure(
                AiFailureKind.RequestRejected,
                "gemini_batch_page_token_invalid",
                isTransient: false);
        }

        var relative = "v1beta/batches?pageSize=100";
        if (!string.IsNullOrEmpty(pageToken))
        {
            relative += $"&pageToken={Uri.EscapeDataString(pageToken)}";
        }

        using var timeout = CreateTimeout(connection, cancellationToken);
        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(connection.BaseAddress, relative));
        AddCredential(
            message,
            Encoding.UTF8.GetString(credentialUtf8.Span));
        using var response = await SendControlAsync(message, timeout.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw FromHttpStatus(response);
        }

        var bytes = await ReadBoundedAsync(
                response.Content,
                MaximumControlResponseBytes,
                timeout.Token)
            .ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var operations = root.TryGetProperty("operations", out var values)
                && values.ValueKind == JsonValueKind.Array
                    ? values.EnumerateArray()
                        .Select(item => ParseStatus(item.GetRawText()))
                        .ToArray()
                    : [];
            return new AiBatchListPage(
                operations,
                GetString(root, "nextPageToken"));
        }
        catch (JsonException exception)
        {
            throw new AiProviderException(
                AiFailureKind.InvalidResponse,
                "gemini_batch_list_json_invalid",
                isTransient: false,
                innerException: exception);
        }
    }

    public async Task CancelAsync(
        AiConnectionSettings connection,
        ReadOnlyMemory<byte> credentialUtf8,
        string providerBatchName,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionAndCredential(connection, credentialUtf8);
        if (!IsBatchName(providerBatchName))
        {
            throw Failure(
                AiFailureKind.RequestRejected,
                "gemini_batch_name_invalid",
                isTransient: false);
        }

        using var timeout = CreateTimeout(connection, cancellationToken);
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(
                connection.BaseAddress,
                $"v1beta/{providerBatchName}:cancel"));
        AddCredential(
            message,
            Encoding.UTF8.GetString(credentialUtf8.Span));
        using var response = await SendControlAsync(message, timeout.Token)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound
            && !response.IsSuccessStatusCode)
        {
            throw FromHttpStatus(response);
        }
    }

    public async Task DeleteBatchAsync(
        AiConnectionSettings connection,
        ReadOnlyMemory<byte> credentialUtf8,
        string providerBatchName,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionAndCredential(connection, credentialUtf8);
        if (!IsBatchName(providerBatchName))
        {
            throw Failure(
                AiFailureKind.RequestRejected,
                "gemini_batch_name_invalid",
                isTransient: false);
        }

        using var timeout = CreateTimeout(connection, cancellationToken);
        using var message = new HttpRequestMessage(
            HttpMethod.Delete,
            new Uri(connection.BaseAddress, $"v1beta/{providerBatchName}"));
        AddCredential(
            message,
            Encoding.UTF8.GetString(credentialUtf8.Span));
        using var response = await SendControlAsync(message, timeout.Token)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound
            && !response.IsSuccessStatusCode)
        {
            throw FromHttpStatus(response);
        }
    }

    public async Task<IReadOnlyList<AiBatchItemResult>> ReadResultsAsync(
        AiConnectionSettings connection,
        ReadOnlyMemory<byte> credentialUtf8,
        AiBatchStatus completedBatch,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionAndCredential(connection, credentialUtf8);
        ArgumentNullException.ThrowIfNull(completedBatch);
        if (completedBatch.State != AiBatchRemoteState.Succeeded)
        {
            throw Failure(
                AiFailureKind.RequestRejected,
                "gemini_batch_not_succeeded",
                isTransient: false);
        }

        if (TryGetInlineResponses(
                completedBatch.RawEnvelope,
                out var inlineResponses))
        {
            return ParseInlineResults(connection, inlineResponses);
        }

        if (!IsFileName(completedBatch.OutputFileName))
        {
            throw Failure(
                AiFailureKind.InvalidResponse,
                "gemini_batch_output_missing",
                isTransient: false);
        }

        var jsonLines = await DownloadResultFileAsync(
                connection,
                credentialUtf8,
                completedBatch.OutputFileName!,
                cancellationToken)
            .ConfigureAwait(false);
        return ParseJsonLineResults(connection, jsonLines);
    }

    public async Task DeleteFileAsync(
        AiConnectionSettings connection,
        ReadOnlyMemory<byte> credentialUtf8,
        string providerFileName,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionAndCredential(connection, credentialUtf8);
        if (!IsFileName(providerFileName))
        {
            throw Failure(
                AiFailureKind.RequestRejected,
                "gemini_file_name_invalid",
                isTransient: false);
        }

        using var timeout = CreateTimeout(connection, cancellationToken);
        using var message = new HttpRequestMessage(
            HttpMethod.Delete,
            new Uri(connection.BaseAddress, $"v1beta/{providerFileName}"));
        AddCredential(
            message,
            Encoding.UTF8.GetString(credentialUtf8.Span));
        using var response = await SendControlAsync(message, timeout.Token)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound
            && !response.IsSuccessStatusCode)
        {
            throw FromHttpStatus(response);
        }
    }

    private async Task<byte[]> DownloadResultFileAsync(
        AiConnectionSettings connection,
        ReadOnlyMemory<byte> credentialUtf8,
        string providerFileName,
        CancellationToken cancellationToken)
    {
        using var timeout = CreateTimeout(connection, cancellationToken);
        var credential = Encoding.UTF8.GetString(credentialUtf8.Span);
        using var metadataRequest = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(connection.BaseAddress, $"v1beta/{providerFileName}"));
        AddCredential(metadataRequest, credential);
        using var metadataResponse = await SendControlAsync(
                metadataRequest,
                timeout.Token)
            .ConfigureAwait(false);
        if (!metadataResponse.IsSuccessStatusCode)
        {
            throw FromHttpStatus(metadataResponse);
        }

        var metadataBytes = await ReadBoundedAsync(
                metadataResponse.Content,
                MaximumControlResponseBytes,
                timeout.Token)
            .ConfigureAwait(false);
        Uri downloadUri;
        try
        {
            using var document = JsonDocument.Parse(metadataBytes);
            var downloadValue = GetString(
                document.RootElement,
                "downloadUri");
            if (!Uri.TryCreate(
                    downloadValue,
                    UriKind.Absolute,
                    out downloadUri!))
            {
                downloadUri = new Uri(
                    connection.BaseAddress,
                    $"download/v1beta/{providerFileName}:download?alt=media");
            }
        }
        catch (JsonException exception)
        {
            throw new AiProviderException(
                AiFailureKind.InvalidResponse,
                "gemini_file_metadata_json_invalid",
                isTransient: false,
                innerException: exception);
        }

        if (downloadUri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(
                downloadUri.Host,
                AllowedHost,
                StringComparison.OrdinalIgnoreCase))
        {
            throw Failure(
                AiFailureKind.InvalidResponse,
                "gemini_file_download_url_invalid",
                isTransient: false);
        }

        using var downloadRequest = new HttpRequestMessage(
            HttpMethod.Get,
            downloadUri);
        AddCredential(downloadRequest, credential);
        using var response = await SendControlAsync(
                downloadRequest,
                timeout.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw FromHttpStatus(response);
        }

        return await ReadBoundedAsync(
                response.Content,
                MaximumResultsBytes,
                timeout.Token)
            .ConfigureAwait(false);
    }

    private static List<AiBatchItemResult> ParseInlineResults(
        AiConnectionSettings connection,
        JsonElement responses)
    {
        var results = new List<AiBatchItemResult>();
        foreach (var item in responses.EnumerateArray())
        {
            var metadata = GetObject(item, "metadata");
            var requestKey = GetString(metadata, "key")
                ?? GetString(item, "key");
            if (string.IsNullOrWhiteSpace(requestKey))
            {
                throw Failure(
                    AiFailureKind.InvalidResponse,
                    "gemini_batch_result_key_missing",
                    isTransient: false);
            }

            results.Add(ParseItem(connection, requestKey, item));
        }

        EnsureUniqueKeys(results);
        return results;
    }

    private static List<AiBatchItemResult> ParseJsonLineResults(
        AiConnectionSettings connection,
        ReadOnlyMemory<byte> jsonLines)
    {
        var results = new List<AiBatchItemResult>();
        using var reader = new StringReader(Encoding.UTF8.GetString(jsonLines.Span));
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var item = document.RootElement;
                var metadata = GetObject(item, "metadata");
                var requestKey = GetString(item, "key")
                    ?? GetString(metadata, "key");
                if (string.IsNullOrWhiteSpace(requestKey))
                {
                    throw Failure(
                        AiFailureKind.InvalidResponse,
                        "gemini_batch_result_key_missing",
                        isTransient: false);
                }

                results.Add(ParseItem(connection, requestKey, item));
            }
            catch (JsonException exception)
            {
                throw new AiProviderException(
                    AiFailureKind.InvalidResponse,
                    "gemini_batch_result_json_invalid",
                    isTransient: false,
                    innerException: exception);
            }
        }

        EnsureUniqueKeys(results);
        return results;
    }

    private static AiBatchItemResult ParseItem(
        AiConnectionSettings connection,
        string requestKey,
        JsonElement item)
    {
        if (item.TryGetProperty("error", out var error)
            && error.ValueKind == JsonValueKind.Object)
        {
            return new AiBatchItemResult(
                requestKey,
                null,
                BoundedProviderError(error));
        }

        var response = item.TryGetProperty("response", out var responseValue)
            ? responseValue
            : item;
        try
        {
            return new AiBatchItemResult(
                requestKey,
                ParseGenerateContentResponse(connection, response),
                null);
        }
        catch (AiProviderException exception)
        {
            return new AiBatchItemResult(
                requestKey,
                null,
                exception.SafeErrorCode);
        }
    }

    private static AiProviderResponse ParseGenerateContentResponse(
        AiConnectionSettings connection,
        JsonElement root)
    {
        var responseId = GetString(root, "responseId");
        var actualModel = GetString(root, "modelVersion");
        if (!root.TryGetProperty("candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array
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
            || parts.ValueKind != JsonValueKind.Array)
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
                && thought.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            if (GetString(part, "text") is { } value)
            {
                text.Append(value);
            }
        }

        if (text.Length is 0 or > MaximumControlResponseBytes)
        {
            throw Failure(
                AiFailureKind.InvalidResponse,
                "gemini_structured_output_missing",
                isTransient: false);
        }

        try
        {
            using var structured = JsonDocument.Parse(text.ToString());
            var usage = root.TryGetProperty(
                "usageMetadata",
                out var usageMetadata)
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
                TimeSpan.Zero);
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

    private static AiBatchStatus ParseStatus(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            return ParseStatus(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new AiProviderException(
                AiFailureKind.InvalidResponse,
                "gemini_batch_status_json_invalid",
                isTransient: false,
                innerException: exception);
        }
    }

    private static AiBatchStatus ParseStatus(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return ParseStatus(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new AiProviderException(
                AiFailureKind.InvalidResponse,
                "gemini_batch_status_json_invalid",
                isTransient: false,
                innerException: exception);
        }
    }

    private static AiBatchStatus ParseStatus(JsonElement root)
    {
        var metadata = GetObject(root, "metadata");
        var response = GetObject(root, "response");
        var resource = response.ValueKind == JsonValueKind.Object
            ? response
            : metadata;
        var name = GetString(root, "name")
            ?? GetString(resource, "name");
        if (!IsBatchName(name))
        {
            throw Failure(
                AiFailureKind.InvalidResponse,
                "gemini_batch_status_name_invalid",
                isTransient: false);
        }

        var stateValue = GetString(metadata, "state")
            ?? GetString(resource, "state");
        var state = ParseRemoteState(stateValue);
        var error = GetObject(root, "error");
        if (state == AiBatchRemoteState.Unspecified
            && GetInt64(error, "code") == 1)
        {
            state = AiBatchRemoteState.Cancelled;
        }

        var output = GetObject(resource, "output");
        var destination = GetObject(resource, "dest");
        var outputFile = GetString(resource, "responsesFile")
            ?? GetString(resource, "responses_file")
            ?? GetString(output, "responsesFile")
            ?? GetString(output, "responses_file")
            ?? GetString(destination, "fileName")
            ?? GetString(destination, "file_name");
        var statsValue = GetObject(resource, "batchStats");
        if (statsValue.ValueKind != JsonValueKind.Object)
        {
            statsValue = GetObject(metadata, "batchStats");
        }

        AiBatchStats? stats = statsValue.ValueKind == JsonValueKind.Object
            ? new AiBatchStats(
                GetInt64(statsValue, "requestCount") ?? 0,
                GetInt64(statsValue, "successfulRequestCount") ?? 0,
                GetInt64(statsValue, "failedRequestCount") ?? 0,
                GetInt64(statsValue, "pendingRequestCount") ?? 0)
            : null;
        return new AiBatchStatus(
            name!,
            GetString(resource, "displayName")
                ?? GetString(metadata, "displayName"),
            state,
            GetDateTimeOffset(resource, "createTime")
                ?? GetDateTimeOffset(metadata, "createTime"),
            GetDateTimeOffset(resource, "updateTime")
                ?? GetDateTimeOffset(metadata, "updateTime"),
            GetDateTimeOffset(resource, "endTime")
                ?? GetDateTimeOffset(metadata, "endTime"),
            stats,
            outputFile,
            error.ValueKind == JsonValueKind.Object
                ? BoundedProviderError(error)
                : null,
            root.Clone());
    }

    private static bool TryGetInlineResponses(
        JsonElement root,
        out JsonElement responses)
    {
        foreach (var resourceName in new[] { "response", "metadata" })
        {
            var resource = GetObject(root, resourceName);
            if (TryGetInlinedResponseArray(
                    resource,
                    "inlinedResponses",
                    out responses)
                || TryGetInlinedResponseArray(
                    resource,
                    "inlined_responses",
                    out responses))
            {
                return true;
            }

            var output = GetObject(resource, "output");
            if (TryGetInlinedResponseArray(
                    output,
                    "inlinedResponses",
                    out responses)
                || TryGetInlinedResponseArray(
                    output,
                    "inlined_responses",
                    out responses))
            {
                return true;
            }

            var destination = GetObject(resource, "dest");
            if (TryGetInlinedResponseArray(
                    destination,
                    "inlinedResponses",
                    out responses)
                || TryGetInlinedResponseArray(
                    destination,
                    "inlined_responses",
                    out responses))
            {
                return true;
            }
        }

        responses = default;
        return false;
    }

    private static bool TryGetInlinedResponseArray(
        JsonElement container,
        string propertyName,
        out JsonElement responses)
    {
        if (container.ValueKind != JsonValueKind.Object
            || !container.TryGetProperty(propertyName, out var value))
        {
            responses = default;
            return false;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            responses = value;
            return true;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var nestedName in new[]
                {
                    "inlinedResponses",
                    "inlined_responses",
                })
            {
                if (value.TryGetProperty(nestedName, out responses)
                    && responses.ValueKind == JsonValueKind.Array)
                {
                    return true;
                }
            }
        }

        responses = default;
        return false;
    }

    private static void WriteGenerateContentRequest(
        Utf8JsonWriter writer,
        AiProviderRequest request)
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

    private static void ValidateProviderRequest(AiProviderRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RequestKey)
            || request.RequestKey.Length > 200
            || request.SystemInstruction.Length is 0 or > 20_000
            || request.UserInstruction.Length is 0 or > 100_000
            || request.Media.Count is 0 or > 32
            || request.Media.Any(media =>
                media.Bytes.IsEmpty
                || media.MimeType is not (
                    "image/png"
                    or "image/jpeg"
                    or "image/webp"
                    or "application/pdf")
                || !Sha256Pattern().IsMatch(media.Sha256))
            || request.MaxOutputTokens is < 64 or > 65_536
            || request.ResponseJsonSchema.ValueKind != JsonValueKind.Object
            || request.MediaResolution is not (
                "MEDIA_RESOLUTION_LOW"
                or "MEDIA_RESOLUTION_MEDIUM"
                or "MEDIA_RESOLUTION_HIGH"))
        {
            throw Failure(
                AiFailureKind.RequestRejected,
                "gemini_batch_request_invalid",
                isTransient: false);
        }
    }

    private static void ValidateConnectionAndCredential(
        AiConnectionSettings connection,
        ReadOnlyMemory<byte> credentialUtf8)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Provider != AiProviders.GeminiDirect
            || connection.BaseAddress.Scheme != Uri.UriSchemeHttps
            || !string.Equals(
                connection.BaseAddress.Host,
                AllowedHost,
                StringComparison.OrdinalIgnoreCase)
            || connection.ModelId != SelectedModel
            || connection.Timeout < TimeSpan.FromSeconds(5)
            || connection.Timeout > TimeSpan.FromMinutes(5))
        {
            throw Failure(
                AiFailureKind.InvalidConfiguration,
                "gemini_batch_configuration_invalid",
                isTransient: false);
        }

        if (credentialUtf8.IsEmpty)
        {
            throw Failure(
                AiFailureKind.Authentication,
                "gemini_credential_missing",
                isTransient: false);
        }
    }

    private static CancellationTokenSource CreateTimeout(
        AiConnectionSettings connection,
        CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(connection.Timeout);
        return timeout;
    }

    private async Task<HttpResponseMessage> SendControlAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            throw new AiProviderException(
                AiFailureKind.Timeout,
                "gemini_batch_timeout",
                isTransient: true,
                innerException: exception);
        }
        catch (HttpRequestException exception)
        {
            throw new AiProviderException(
                AiFailureKind.TransientProvider,
                "gemini_batch_network_error",
                isTransient: true,
                innerException: exception);
        }
    }

    private static ByteArrayContent JsonContent(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };
        return content;
    }

    private static void AddCredential(
        HttpRequestMessage message,
        string credential)
    {
        message.Headers.TryAddWithoutValidation("x-goog-api-key", credential);
        message.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes)
        {
            throw Failure(
                AiFailureKind.InvalidResponse,
                "gemini_batch_response_too_large",
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
                        "gemini_batch_response_too_large",
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

    private static AiProviderException FromHttpStatus(
        HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter?.Delta;
        return response.StatusCode switch
        {
            HttpStatusCode.BadRequest => Failure(
                AiFailureKind.RequestRejected,
                "gemini_batch_request_invalid",
                isTransient: false),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Failure(
                AiFailureKind.Authentication,
                "gemini_authentication_failed",
                isTransient: false),
            HttpStatusCode.NotFound => Failure(
                AiFailureKind.InvalidConfiguration,
                "gemini_batch_not_found",
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
                "gemini_batch_http_"
                + ((int)response.StatusCode).ToString(
                    CultureInfo.InvariantCulture),
                isTransient: false),
        };
    }

    private static AiBatchRemoteState ParseRemoteState(string? value) =>
        value switch
        {
            "BATCH_STATE_PENDING" or "JOB_STATE_PENDING" =>
                AiBatchRemoteState.Pending,
            "BATCH_STATE_RUNNING" or "JOB_STATE_RUNNING" =>
                AiBatchRemoteState.Running,
            "BATCH_STATE_SUCCEEDED" or "JOB_STATE_SUCCEEDED" =>
                AiBatchRemoteState.Succeeded,
            "BATCH_STATE_FAILED" or "JOB_STATE_FAILED" =>
                AiBatchRemoteState.Failed,
            "BATCH_STATE_CANCELLED" or "JOB_STATE_CANCELLED" =>
                AiBatchRemoteState.Cancelled,
            "BATCH_STATE_EXPIRED" or "JOB_STATE_EXPIRED" =>
                AiBatchRemoteState.Expired,
            _ => AiBatchRemoteState.Unspecified,
        };

    private static string BoundedProviderError(JsonElement error)
    {
        var code = GetInt64(error, "code");
        return code is null
            ? "gemini_batch_item_failed"
            : "gemini_batch_item_"
              + code.Value.ToString(CultureInfo.InvariantCulture);
    }

    private static void EnsureUniqueKeys(
        List<AiBatchItemResult> results)
    {
        if (results.Select(item => item.RequestKey)
            .Distinct(StringComparer.Ordinal)
            .Count() != results.Count)
        {
            throw Failure(
                AiFailureKind.InvalidResponse,
                "gemini_batch_result_key_duplicate",
                isTransient: false);
        }
    }

    private static JsonElement GetObject(
        JsonElement value,
        string propertyName) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.Object
            ? property
            : default;

    private static string? GetString(
        JsonElement value,
        string propertyName) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? GetInt32(
        JsonElement value,
        string propertyName) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(propertyName, out var property)
        && property.TryGetInt32(out var result)
            ? result
            : null;

    private static long? GetInt64(
        JsonElement value,
        string propertyName)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.TryGetInt64(out var number))
        {
            return number;
        }

        return property.ValueKind == JsonValueKind.String
            && long.TryParse(
                property.GetString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out number)
                ? number
                : null;
    }

    private static DateTimeOffset? GetDateTimeOffset(
        JsonElement value,
        string propertyName) =>
        GetString(value, propertyName) is { } text
        && DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var instant)
            ? instant
            : null;

    private static bool IsBatchName(string? value) =>
        value is not null && BatchNamePattern().IsMatch(value);

    private static bool IsFileName(string? value) =>
        value is not null && FileNamePattern().IsMatch(value);

    private static AiProviderException Failure(
        AiFailureKind kind,
        string safeErrorCode,
        bool isTransient) =>
        new(kind, safeErrorCode, isTransient);

    [GeneratedRegex(
        "^ooki-[0-9A-Z]{26}-[0-9a-f]{12}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex DisplayNamePattern();

    [GeneratedRegex(
        "^batches/[A-Za-z0-9._-]{1,200}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex BatchNamePattern();

    [GeneratedRegex(
        "^files/[a-z0-9][a-z0-9-]{0,198}[a-z0-9]$|^files/[a-z0-9]$",
        RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();

    [GeneratedRegex(
        "^[0-9a-f]{64}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}
