using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Ai.Gemini;

namespace OokiGrader.Host.Jobs;

/// <summary>
/// Code-owned task routing for the default Gemini connection. RequestedModel
/// remains the preferred model; ActualModel records the dispatched model.
/// Only an explicit quota rejection permits retrying important work on Lite.
/// </summary>
internal sealed class GeminiTaskRoutingClient(
    IAiProviderClient direct, GeminiQuotaCooldownStore cooldowns) : IAiProviderClient
{
    internal const string LightTaskReason = "gemini_light_task";
    internal const string QuotaReason = "gemini_quota_fallback";
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    public string Provider => AiProviders.GeminiDirect;

    public async Task<AiProviderResponse> GenerateAsync(
        AiConnectionSettings connection, ReadOnlyMemory<byte> credentialUtf8,
        AiProviderRequest request, CancellationToken cancellationToken = default)
    {
        if (connection.Provider != Provider
            || connection.ModelId != AiProviderCatalog.GeminiDefaultModelId
            || credentialUtf8.IsEmpty)
            return await direct.GenerateAsync(connection, credentialUtf8, request,
                cancellationToken).ConfigureAwait(false);

        if (request.TaskType == AiTaskTypes.NameTranscription)
            return await GenerateLightAsync(connection, credentialUtf8, request,
                LightTaskReason, cancellationToken).ConfigureAwait(false);

        if (request.TaskType is not (AiTaskTypes.TemplateExtraction
            or AiTaskTypes.InitialGrading or AiTaskTypes.Adjudication))
            return await direct.GenerateAsync(connection, credentialUtf8, request,
                cancellationToken).ConfigureAwait(false);

        var key = CredentialScope(credentialUtf8);
        // Serialize primary dispatches for one key so queued work observes a
        // quota rejection before spending another request on the same model.
        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await cooldowns.IsCoolingDownAsync(key, cancellationToken))
            {
                try
                {
                    return await direct.GenerateAsync(connection, credentialUtf8,
                        request, cancellationToken).ConfigureAwait(false);
                }
                catch (AiProviderException exception)
                    when (exception.Kind == AiFailureKind.RateLimited)
                {
                    await cooldowns.RecordAsync(key, exception.RetryAfter,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally { gate.Release(); }

        return await GenerateLightAsync(connection, credentialUtf8, request,
            QuotaReason, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AiCapabilityProbeResult> ProbeAsync(
        AiConnectionSettings connection, ReadOnlyMemory<byte> credentialUtf8,
        CancellationToken cancellationToken = default)
    {
        if (connection.ModelId != AiProviderCatalog.GeminiDefaultModelId
            || credentialUtf8.IsEmpty)
            return await direct.ProbeAsync(connection, credentialUtf8,
                cancellationToken).ConfigureAwait(false);

        var key = CredentialScope(credentialUtf8);
        var coolingDown = await cooldowns.IsCoolingDownAsync(key, cancellationToken);
        if (!coolingDown)
        {
            var primary = await direct.ProbeAsync(connection, credentialUtf8,
                cancellationToken).ConfigureAwait(false);
            if (primary.State != "passed")
            {
                if (primary.SafeErrorCode != "gemini_rate_limited") return primary;
                await cooldowns.RecordAsync(key, primary.RetryAfter,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        // Lite must pass the same image/JSON capability check. The default
        // connection can remain usable while its preferred model is throttled.
        return await direct.ProbeAsync(connection with
        { ModelId = AiProviderCatalog.GeminiLightModelId }, credentialUtf8,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AiProviderResponse> GenerateLightAsync(
        AiConnectionSettings connection, ReadOnlyMemory<byte> credential,
        AiProviderRequest request, string reason, CancellationToken token)
    {
        var settings = connection with { ModelId = AiProviderCatalog.GeminiLightModelId };
        var response = await direct.GenerateAsync(settings, credential, request, token)
            .ConfigureAwait(false);
        AiResponseMetadataValidator.Validate(response, Provider, settings.ModelId);
        return response with
        {
            RequestedModel = connection.ModelId,
            ActualModel = response.ActualModel ?? settings.ModelId,
            ModelRoutingReason = reason,
        };
    }

    internal static string CredentialScope(ReadOnlyMemory<byte> credential) =>
        Convert.ToHexString(SHA256.HashData(credential.Span)).ToLowerInvariant();
}

/// <summary>
/// Durable cooldowns contain only credential digests and expiry times. Provider
/// RetryInfo/daily quota details are authoritative; no universal 20/day counter.
/// Expired cooldowns automatically allow the next preferred-model request.
/// </summary>
internal sealed class GeminiQuotaCooldownStore(string directory, TimeProvider clock)
{
    public async Task<bool> IsCoolingDownAsync(string key, CancellationToken token)
    {
        try
        {
            var text = await File.ReadAllTextAsync(PathFor(key), token).ConfigureAwait(false);
            return JsonSerializer.Deserialize<DateTimeOffset>(text) > clock.GetUtcNow();
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (JsonException) { return false; }
    }

    public async Task RecordAsync(string key, TimeSpan? delay, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        var until = clock.GetUtcNow() + TimeSpan.FromSeconds(
            Math.Clamp(delay?.TotalSeconds ?? 60, 1, 90_000));
        var path = PathFor(key);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(until), token)
                .ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private string PathFor(string key)
    {
        if (key.Length != 64 || key.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Invalid quota scope.", nameof(key));
        return Path.Combine(directory, key + ".json");
    }
}
