using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Application.Identifiers;

namespace OokiGrader.Host.Jobs;

public sealed record GeminiBatchCapabilityProbeResult(
    bool Available,
    bool CleanupSucceeded,
    string State,
    string? SafeErrorCode,
    TimeSpan Latency);

/// <summary>
/// Exercises the real asynchronous Batch surface with one tiny immutable
/// request. Creation is attempted once; an ambiguous result is reconciled by
/// its globally unique display name before any cleanup is attempted.
/// </summary>
public sealed class GeminiBatchCapabilityProbe(
    IAiBatchProviderClient batchProvider,
    TimeProvider timeProvider)
{
    private static readonly byte[] ProbePng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    public async Task<GeminiBatchCapabilityProbeResult> ProbeAsync(
        AiConnectionSettings connection,
        ReadOnlyMemory<byte> credentialUtf8,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var boundedConnection = connection with
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(
                connection.Timeout.TotalSeconds,
                5,
                20)),
        };
        using var overallTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        overallTimeout.CancelAfter(TimeSpan.FromSeconds(45));
        var stopwatch = Stopwatch.StartNew();
        var now = timeProvider.GetUtcNow();
        var probeId = UlidId.New(now);
        var requestKey = $"probe_{probeId}";
        using var schema = JsonDocument.Parse(
            """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "ok": {"type": "boolean"}
              },
              "required": ["ok"]
            }
            """);
        var mediaHash = Sha256(ProbePng);
        var request = new AiProviderRequest(
            requestKey,
            AiTaskTypes.InitialGrading,
            "batch-capability-v1",
            "batch-capability-schema-v1",
            "Return only the requested JSON object.",
            "Return {\"ok\":true}. This is a capability probe.",
            schema.RootElement.Clone(),
            [
                new AiMediaPart(
                    "image/png",
                    ProbePng,
                    mediaHash),
            ],
            MaxOutputTokens: 64,
            MediaResolution: "MEDIA_RESOLUTION_LOW");
        var jsonLines = batchProvider.BuildJsonLines([request]);
        var manifestHash = Sha256(jsonLines);
        var displayName = $"ooki-{probeId}-{manifestHash[..12]}";
        AiBatchInputFile? inputFile = null;
        AiBatchCreateReceipt? receipt = null;
        AiBatchStatus? status = null;
        var cancelIssued = false;
        var available = false;
        string? safeErrorCode = null;
        var cleanupSucceeded = true;
        try
        {
            inputFile = await batchProvider.UploadJsonLinesAsync(
                    boundedConnection,
                    credentialUtf8,
                    displayName,
                    jsonLines,
                    overallTimeout.Token)
                .ConfigureAwait(false);
            receipt = await CreateOrReconcileAsync(
                    boundedConnection,
                    credentialUtf8,
                    displayName,
                    manifestHash,
                    inputFile.ProviderFileName,
                    now,
                    overallTimeout.Token)
                .ConfigureAwait(false);
            status = await batchProvider.GetAsync(
                    boundedConnection,
                    credentialUtf8,
                    receipt.ProviderBatchName,
                    overallTimeout.Token)
                .ConfigureAwait(false);
            if (status.State == AiBatchRemoteState.Succeeded)
            {
                var results = await batchProvider.ReadResultsAsync(
                        boundedConnection,
                        credentialUtf8,
                        status,
                        overallTimeout.Token)
                    .ConfigureAwait(false);
                ValidateCompletedProbe(results, requestKey);
                available = true;
            }
            else if (status.State is AiBatchRemoteState.Pending
                     or AiBatchRemoteState.Running
                     or AiBatchRemoteState.Unspecified)
            {
                await batchProvider.CancelAsync(
                        boundedConnection,
                        credentialUtf8,
                        receipt.ProviderBatchName,
                        overallTimeout.Token)
                    .ConfigureAwait(false);
                cancelIssued = true;
                status = await batchProvider.GetAsync(
                        boundedConnection,
                        credentialUtf8,
                        receipt.ProviderBatchName,
                        overallTimeout.Token)
                    .ConfigureAwait(false);
                if (status.State == AiBatchRemoteState.Succeeded)
                {
                    var results = await batchProvider.ReadResultsAsync(
                            boundedConnection,
                            credentialUtf8,
                            status,
                            overallTimeout.Token)
                        .ConfigureAwait(false);
                    ValidateCompletedProbe(results, requestKey);
                    available = true;
                }
                else
                {
                    available = status.State is
                        AiBatchRemoteState.Cancelled
                        or AiBatchRemoteState.Pending
                        or AiBatchRemoteState.Running;
                }

                if (!available)
                {
                    safeErrorCode =
                        status.SafeErrorCode
                        ?? "gemini_batch_probe_terminal_failure";
                }
            }
            else
            {
                safeErrorCode =
                    status.SafeErrorCode
                    ?? "gemini_batch_probe_terminal_failure";
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AiBatchCreateException exception)
        {
            safeErrorCode = exception.SafeErrorCode;
        }
        catch (AiProviderException exception)
        {
            safeErrorCode = exception.SafeErrorCode;
        }
        catch (InvalidDataException exception)
        {
            safeErrorCode = exception.Message;
        }
        finally
        {
            using var cleanupTimeout =
                new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (receipt is not null)
            {
                if (!cancelIssued
                    && status?.State is not (
                        AiBatchRemoteState.Succeeded
                        or AiBatchRemoteState.Failed
                        or AiBatchRemoteState.Cancelled
                        or AiBatchRemoteState.Expired))
                {
                    cleanupSucceeded &= await TryCleanupAsync(() =>
                        batchProvider.CancelAsync(
                            boundedConnection,
                            credentialUtf8,
                            receipt.ProviderBatchName,
                            cleanupTimeout.Token));
                }

                cleanupSucceeded &= await TryCleanupAsync(() =>
                    batchProvider.DeleteBatchAsync(
                        boundedConnection,
                        credentialUtf8,
                        receipt.ProviderBatchName,
                        cleanupTimeout.Token));
            }
            else if (inputFile is not null
                     && safeErrorCode?.Contains(
                         "ambiguous",
                         StringComparison.Ordinal) == true)
            {
                cleanupSucceeded = false;
            }

            var files = new[]
                {
                    status?.OutputFileName,
                    inputFile?.ProviderFileName,
                }
                .Where(item => item is not null)
                .Distinct(StringComparer.Ordinal)
                .Cast<string>();
            foreach (var file in files)
            {
                cleanupSucceeded &= await TryCleanupAsync(() =>
                    batchProvider.DeleteFileAsync(
                        boundedConnection,
                        credentialUtf8,
                        file,
                        cleanupTimeout.Token));
            }
        }

        stopwatch.Stop();
        if (!cleanupSucceeded)
        {
            available = false;
            safeErrorCode = "gemini_batch_probe_cleanup_failed";
        }

        return new GeminiBatchCapabilityProbeResult(
            available,
            cleanupSucceeded,
            available ? "passed" : "failed",
            available
                ? null
                : safeErrorCode ?? "gemini_batch_probe_failed",
            stopwatch.Elapsed);
    }

    private async Task<AiBatchCreateReceipt> CreateOrReconcileAsync(
        AiConnectionSettings connection,
        ReadOnlyMemory<byte> credentialUtf8,
        string displayName,
        string manifestHash,
        string inputFileName,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            return await batchProvider.CreateAsync(
                    connection,
                    credentialUtf8,
                    new AiBatchCreateRequest(
                        displayName,
                        manifestHash,
                        inputFileName,
                        1),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AiBatchCreateException exception)
            when (exception.Kind
                == AiBatchCreateFailureKind.AmbiguousAfterSend)
        {
            var matches = new List<AiBatchStatus>();
            string? pageToken = null;
            for (var page = 0; page < 3; page++)
            {
                var result = await batchProvider.ListAsync(
                        connection,
                        credentialUtf8,
                        pageToken,
                        cancellationToken)
                    .ConfigureAwait(false);
                matches.AddRange(result.Batches.Where(item =>
                    item.DisplayName == displayName
                    && (item.CreatedAt is null
                        || (item.CreatedAt >= startedAt.AddMinutes(-2)
                            && item.CreatedAt
                                <= startedAt.AddMinutes(10)))));
                pageToken = result.NextPageToken;
                if (string.IsNullOrEmpty(pageToken))
                {
                    break;
                }
            }

            var unique = matches
                .DistinctBy(item => item.ProviderBatchName)
                .ToArray();
            if (unique.Length != 1)
            {
                throw new AiProviderException(
                    AiFailureKind.InvalidResponse,
                    unique.Length == 0
                        ? "gemini_batch_probe_create_ambiguous"
                        : "gemini_batch_probe_multiple_matches",
                    isTransient: false,
                    innerException: exception);
            }

            return new AiBatchCreateReceipt(
                unique[0].ProviderBatchName,
                displayName,
                unique[0].CreatedAt);
        }
    }

    private static void ValidateCompletedProbe(
        IReadOnlyList<AiBatchItemResult> results,
        string requestKey)
    {
        var item = results.Count == 1 ? results[0] : null;
        if (item?.RequestKey != requestKey
            || item.Response is null
            || !AiResponseMetadataValidator.IsAccepted(item.Response)
            || item.Response.StructuredOutput.ValueKind
                != JsonValueKind.Object
            || !item.Response.StructuredOutput.TryGetProperty(
                "ok",
                out var ok)
            || ok.ValueKind != JsonValueKind.True)
        {
            throw new InvalidDataException(
                "gemini_batch_probe_response_invalid");
        }
    }

    private static async Task<bool> TryCleanupAsync(Func<Task> cleanup)
    {
        try
        {
            await cleanup().ConfigureAwait(false);
            return true;
        }
        catch (AiProviderException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static string Sha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}
