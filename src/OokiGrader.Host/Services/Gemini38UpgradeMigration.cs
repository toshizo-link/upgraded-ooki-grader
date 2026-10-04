using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Host.Api;
using OokiGrader.Host.Jobs;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Services;

/// <summary>
/// The 0.9.17 upgrade selects the Gemini 3.8/3.5 routing policy before queued AI work
/// can run. Credentials and historical requests remain untouched. A real
/// capability probe, rather than the old model's probe, controls readiness.
/// </summary>
internal static class Gemini38UpgradeMigration
{
    internal const string ModelId = "gemini-3.8-flash";
    internal const string AuditEventType = "ai.upgrade.gemini38.0_9_17";

    public static async Task<bool> ApplyAsync(
        OokiGraderDbContext db,
        IAiPromptBundleCatalog promptCatalog,
        TimeProvider timeProvider,
        IAiProviderFeaturePolicy featurePolicy,
        IAiSecretStore secretStore,
        IAiProviderClientResolver providerResolver,
        CancellationToken cancellationToken = default)
    {
        var connection = await db.AiConnections
            .Where(item => item.Provider == AiProviders.GeminiDirect)
            .OrderByDescending(item => item.State != "disabled")
            .ThenByDescending(item => item.UpdatedAt)
            .ThenByDescending(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (connection is null
            || await db.AuditEvents.AnyAsync(
                item => item.EventType == AuditEventType
                    && item.ObjectId == connection.Id,
                cancellationToken))
        {
            return false;
        }

        var previousModelId = connection.ModelId;
        // A model switch does not rotate or rewrite the stored API key. Its
        // credential revision and opaque secret reference must stay aligned.
        connection.ModelId = ModelId;
        connection.EndpointProfile = AiProviderCatalog.GeminiEndpointProfile;
        connection.TimeoutSeconds = 300;
        var result = await ProbeAsync(
            connection,
            featurePolicy,
            secretStore,
            providerResolver,
            cancellationToken);
        var now = timeProvider.GetUtcNow();
        var ready = AiAdminEndpoints.IsSuccessfulImageProbe(connection, result);

        await using var transaction = await db.Database.BeginTransactionAsync(
            cancellationToken);
        AiAdminEndpoints.ApplyProbeResult(connection, result, now);
        db.AiCapabilityProbes.Add(AiAdminEndpoints.BuildProbe(
            connection,
            result,
            now));
        await AiAdminEndpoints.ReconcileCurrentProfilesAsync(
            db,
            connection,
            connection.CreatedByStaffUserId,
            promptCatalog,
            now,
            replaceOtherProviders: true,
            cancellationToken: cancellationToken,
            activate: ready);
        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = UlidId.New(now),
            OccurredAt = now,
            EventType = AuditEventType,
            ObjectType = "ai_connection",
            ObjectId = connection.Id,
            Outcome = "succeeded",
            SafeMetadataJson = JsonSerializer.Serialize(new
            {
                previousModelId,
                modelId = ModelId,
                connection.CredentialRevision,
                capabilityProbeState = result.State,
                capabilityProbeErrorCode = result.SafeErrorCode,
            }),
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task<AiCapabilityProbeResult> ProbeAsync(
        AiConnectionEntity connection,
        IAiProviderFeaturePolicy featurePolicy,
        IAiSecretStore secretStore,
        IAiProviderClientResolver providerResolver,
        CancellationToken cancellationToken)
    {
        if (!featurePolicy.IsEnabled(connection.Provider))
        {
            return FailedProbe("ai_provider_feature_disabled");
        }

        if (string.IsNullOrWhiteSpace(connection.SecretReference))
        {
            return FailedProbe("ai_secret_unavailable");
        }

        try
        {
            using var secret = await secretStore.ReadAsync(
                new AiSecretReference(connection.SecretReference),
                cancellationToken);
            var settings = new AiConnectionSettings(
                connection.Id,
                connection.Provider,
                AiProviderCatalog.GeminiBaseAddress,
                ModelId,
                // Keep a network outage from holding up the Windows service's
                // upgrade startup. The configured request timeout is retained.
                TimeSpan.FromSeconds(Math.Clamp(connection.TimeoutSeconds, 5, 25)));
            return await providerResolver.GetRequired(connection.Provider)
                .ProbeAsync(settings, secret.Utf8Bytes, cancellationToken);
        }
        catch (AiProviderException exception)
        {
            return FailedProbe(exception.SafeErrorCode);
        }
        catch (KeyNotFoundException)
        {
            return FailedProbe("ai_secret_unavailable");
        }
        catch (Exception exception) when (exception is IOException
            or CryptographicException
            or FormatException
            or ArgumentException)
        {
            return FailedProbe("ai_secret_unavailable");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return FailedProbe("gemini_timeout");
        }
    }

    private static AiCapabilityProbeResult FailedProbe(string safeErrorCode) =>
        new(false, false, false, false, false, "failed", safeErrorCode, null);
}
