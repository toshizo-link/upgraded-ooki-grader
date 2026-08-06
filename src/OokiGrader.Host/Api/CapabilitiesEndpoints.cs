using Microsoft.EntityFrameworkCore;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Host.Jobs;
using OokiGrader.Infrastructure.Persistence;

namespace OokiGrader.Host.Api;

internal static class CapabilitiesEndpoints
{
    private const string SelectedModel = AiProviderRuntime.GeminiModel;

    public static IEndpointRouteBuilder MapCapabilitiesEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/capabilities", GetAsync)
            .WithTags("Capabilities");
        return endpoints;
    }

    internal static async Task<RuntimeCapabilities> ReadAsync(
        OokiGraderDbContext db,
        IConfiguration configuration,
        IAiPromptBundleCatalog promptCatalog,
        CancellationToken cancellationToken)
    {
        var profiles = await db.AiTaskProfiles
            .AsNoTracking()
            .Where(profile =>
                profile.Active
                && (profile.ApprovalState == "pilot_approved"
                    || profile.ApprovalState == "production_approved"))
            .Select(profile => new ReadyProfile(
                profile.TaskType,
                profile.ProcessingStrategy,
                profile.ModelId,
                profile.ConnectionRevision,
                profile.PromptVersion,
                profile.SchemaVersion,
                profile.PromptContentHash,
                profile.AiConnection.Provider,
                profile.AiConnection.EndpointProfile,
                profile.AiConnection.ModelId,
                profile.AiConnection.CredentialRevision,
                profile.AiConnection.State,
                profile.AiConnection.LastCapabilityProbeState,
                profile.AiConnection.LastBatchCapabilityProbeState,
                profile.AiConnection
                    .LastBatchCapabilityProbeCredentialRevision))
            .ToArrayAsync(cancellationToken);

        var templateEnabled = configuration.GetValue(
            "Features:Ai.TemplateGeneration",
            false);
        var semanticEnabled = configuration.GetValue(
            "Features:Grading.Semantic",
            false);
        var reportsEnabled = configuration.GetValue(
            "Features:Reports.Pdf",
            false);
        var geminiDirectEnabled = configuration.GetValue(
            "Features:Ai.GeminiDirect",
            false);
        var openRouterEnabled = configuration.GetValue(
            "Features:Ai.OpenRouter",
            false);
        var standardAiEnabled = geminiDirectEnabled || openRouterEnabled;
        return new RuntimeCapabilities(
            new RuntimeReportCapabilities(reportsEnabled),
            new RuntimeAiCapabilities(
                Provider: AiProviders.GeminiDirect,
                ModelId: SelectedModel,
                TemplateGeneration: new RuntimeFeatureCapability(
                    templateEnabled && standardAiEnabled,
                    templateEnabled
                    && standardAiEnabled
                    && profiles.Any(profile => IsReady(
                        profile,
                        AiTaskTypes.TemplateExtraction,
                        promptCatalog.GetRequired(
                            AiTaskTypes.TemplateExtraction),
                        geminiDirectEnabled,
                        openRouterEnabled))),
                NameTranscription: new RuntimeFeatureCapability(
                    Enabled: standardAiEnabled,
                    Ready: standardAiEnabled
                    && profiles.Any(profile => IsReady(
                        profile,
                        AiTaskTypes.NameTranscription,
                        promptCatalog.GetRequired(
                            AiTaskTypes.NameTranscription),
                        geminiDirectEnabled,
                        openRouterEnabled))),
                SemanticGrading: new RuntimeFeatureCapability(
                    semanticEnabled && standardAiEnabled,
                    semanticEnabled
                    && standardAiEnabled
                    && profiles.Any(profile => IsReady(
                        profile,
                        AiTaskTypes.InitialGrading,
                        promptCatalog.GetRequired(
                            AiTaskTypes.InitialGrading),
                        geminiDirectEnabled,
                        openRouterEnabled))),
                GeminiBatch: new RuntimeFeatureCapability(
                    Enabled: semanticEnabled && geminiDirectEnabled,
                    Ready: semanticEnabled
                    && geminiDirectEnabled
                    && profiles.Any(profile =>
                        IsReady(
                            profile,
                            AiTaskTypes.InitialGrading,
                            promptCatalog.GetRequired(
                                AiTaskTypes.InitialGrading),
                            geminiDirectEnabled,
                            openRouterEnabled)
                        && profile.Provider == AiProviders.GeminiDirect
                        && profile.ProcessingStrategy == "gemini_batch"
                        && profile.BatchProbeState == "passed"
                        && profile.BatchProbeCredentialRevision
                            == profile.ConnectionCredentialRevision)),
                OpenRouterEnabled: openRouterEnabled),
            new RuntimeSafetyCapabilities(
                configuration.GetValue(
                    "Features:Recognition.AutoAssign",
                    false),
                configuration.GetValue(
                    "Features:Grading.AutoFinalize",
                    false)));
    }

    private static async Task<IResult> GetAsync(
        OokiGraderDbContext db,
        IConfiguration configuration,
        IAiPromptBundleCatalog promptCatalog,
        CancellationToken cancellationToken)
    {
        var capabilities = await ReadAsync(
            db,
            configuration,
            promptCatalog,
            cancellationToken);
        return Results.Ok(capabilities);
    }

    private static bool IsReady(
        ReadyProfile profile,
        string taskType,
        AiPromptBundle bundle,
        bool geminiDirectEnabled,
        bool openRouterEnabled) =>
        profile.TaskType == taskType
        && profile.ProfileModelId == profile.ConnectionModelId
        && profile.ProfileConnectionRevision
            == profile.ConnectionCredentialRevision
        && ((profile.Provider == AiProviders.GeminiDirect
                && geminiDirectEnabled)
            || (profile.Provider == AiProviders.OpenRouter
                && openRouterEnabled))
        && AiProviderCatalog.IsConnectionShapeValid(
            profile.Provider,
            profile.EndpointProfile,
            profile.ConnectionModelId)
        && AiProviderCatalog.SupportsImageTasks(
            profile.Provider,
            profile.ConnectionModelId)
        && profile.ConnectionState == "active"
        && profile.ProbeState == "passed"
        && (profile.ProcessingStrategy != "gemini_batch"
            || (profile.BatchProbeState == "passed"
                && profile.BatchProbeCredentialRevision
                    == profile.ConnectionCredentialRevision))
        && profile.PromptVersion == bundle.PromptVersion
        && profile.SchemaVersion == bundle.SchemaVersion
        && profile.PromptContentHash == bundle.ContentHash;

    private sealed record ReadyProfile(
        string TaskType,
        string ProcessingStrategy,
        string ProfileModelId,
        long ProfileConnectionRevision,
        string PromptVersion,
        string SchemaVersion,
        string PromptContentHash,
        string Provider,
        string EndpointProfile,
        string ConnectionModelId,
        int ConnectionCredentialRevision,
        string ConnectionState,
        string? ProbeState,
        string? BatchProbeState,
        int? BatchProbeCredentialRevision);
}

internal sealed record RuntimeCapabilities(
    RuntimeReportCapabilities Reports,
    RuntimeAiCapabilities Ai,
    RuntimeSafetyCapabilities Safety);

internal sealed record RuntimeReportCapabilities(bool PdfExport);

internal sealed record RuntimeAiCapabilities(
    string Provider,
    string ModelId,
    RuntimeFeatureCapability TemplateGeneration,
    RuntimeFeatureCapability NameTranscription,
    RuntimeFeatureCapability SemanticGrading,
    RuntimeFeatureCapability GeminiBatch,
    bool OpenRouterEnabled);

internal sealed record RuntimeFeatureCapability(bool Enabled, bool Ready);

internal sealed record RuntimeSafetyCapabilities(
    bool AutomaticAssignment,
    bool AutomaticFinalization);
