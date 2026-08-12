using Microsoft.EntityFrameworkCore;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Host.Jobs;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Services;

/// <summary>
/// Selects the one current template-extraction profile and applies the same
/// runtime eligibility rules before work is queued and again when it is run.
/// </summary>
internal static class TemplateExtractionAiProfilePolicy
{
    public static async Task<TemplateExtractionAiProfileSelection?>
        FindCurrentUsableAsync(
            OokiGraderDbContext db,
            IAiPromptBundleCatalog promptCatalog,
            IAiProviderFeaturePolicy providerFeaturePolicy,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(promptCatalog);
        ArgumentNullException.ThrowIfNull(providerFeaturePolicy);

        var bundle = promptCatalog.GetRequired(AiTaskTypes.TemplateExtraction);
        var profile = await db.AiTaskProfiles
            .AsNoTracking()
            .Include(item => item.AiConnection)
            .SingleOrDefaultAsync(
                item => item.TaskType == AiTaskTypes.TemplateExtraction
                    && item.Active,
                cancellationToken)
            .ConfigureAwait(false);
        return profile is not null
            && IsUsable(profile, bundle, providerFeaturePolicy)
                ? new TemplateExtractionAiProfileSelection(profile, bundle)
                : null;
    }

    public static bool IsUsable(
        AiTaskProfileEntity profile,
        AiPromptBundle bundle,
        IAiProviderFeaturePolicy providerFeaturePolicy)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(providerFeaturePolicy);

        return profile.TaskType == AiTaskTypes.TemplateExtraction
            && profile.Active
            && AiTaskProfileRuntimePolicy.IsReadyApprovalState(
                profile.ApprovalState)
            && AiProviderCatalog.IsSupportedProvider(
                profile.AiConnection.Provider)
            && providerFeaturePolicy.IsEnabled(profile.AiConnection.Provider)
            && profile.ModelId == profile.AiConnection.ModelId
            && AiProviderCatalog.SupportsImageTasks(
                profile.AiConnection.Provider,
                profile.ModelId)
            && profile.AiConnection.EndpointProfile
                == AiProviderCatalog.GetEndpointProfile(
                    profile.AiConnection.Provider)
            && profile.AiConnection.State == "active"
            && profile.AiConnection.LastCapabilityProbeState == "passed"
            && profile.ConnectionRevision
                == profile.AiConnection.CredentialRevision
            && profile.PromptVersion == bundle.PromptVersion
            && profile.SchemaVersion == bundle.SchemaVersion
            && profile.PromptContentHash == bundle.ContentHash
            && profile.ThinkingLevel == "medium"
            && profile.ProcessingStrategy is
                "queued_standard" or "expedite_standard";
    }
}

internal sealed record TemplateExtractionAiProfileSelection(
    AiTaskProfileEntity Profile,
    AiPromptBundle Bundle);
