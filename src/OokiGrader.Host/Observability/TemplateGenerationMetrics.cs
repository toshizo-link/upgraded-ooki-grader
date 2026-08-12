using System.Diagnostics;
using System.Diagnostics.Metrics;
using OokiGrader.Domain.Templates;

namespace OokiGrader.Host.Observability;

/// <summary>
/// Bounded operational metrics for deterministic template generation.
/// Never add source, batch, unit, file, or template identifiers to these tags.
/// AI cost remains authoritative in the durable request/usage ledger.
/// </summary>
internal static class TemplateGenerationMetrics
{
    internal const string MeterName = "OokiGrader.TemplateGeneration";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    private static readonly Counter<long> Batches = Meter.CreateCounter<long>(
        "ookigrader.template_generation.batches",
        unit: "{batch}",
        description: "Durably created deterministic template-generation batches.");

    private static readonly Histogram<long> PlannedUnits = Meter.CreateHistogram<long>(
        "ookigrader.template_generation.planned_units",
        unit: "{unit}",
        description: "Number of deterministic units planned per created batch.");

    private static readonly Counter<long> StepPageCountRejections =
        Meter.CreateCounter<long>(
            "ookigrader.template_generation.step_page_count_rejections",
            unit: "{rejection}",
            description: "STEP sources rejected because the page count is not divisible by six.");

    private static readonly Counter<long> ExtractionCalls = Meter.CreateCounter<long>(
        "ookigrader.template_generation.extraction_calls",
        unit: "{call}",
        description: "Provider extraction calls dispatched for deterministic units.");

    private static readonly Counter<long> OrientationRetries = Meter.CreateCounter<long>(
        "ookigrader.template_generation.orientation_retries",
        unit: "{retry}",
        description: "Automatic orientation retries started after a valid rotate response.");

    private static readonly Counter<long> OrientationRetryFailures =
        Meter.CreateCounter<long>(
            "ookigrader.template_generation.orientation_retry_failures",
            unit: "{failure}",
            description: "Units that failed after starting their single orientation retry.");

    private static readonly Counter<long> UnitExtractionSuccesses =
        Meter.CreateCounter<long>(
            "ookigrader.template_generation.unit_extraction_successes",
            unit: "{unit}",
            description: "Deterministic units whose extraction completed successfully.");

    private static readonly Counter<long> UnitExtractionFailures =
        Meter.CreateCounter<long>(
            "ookigrader.template_generation.unit_extraction_failures",
            unit: "{unit}",
            description: "Deterministic units whose extraction ended in failure.");

    private static readonly Counter<long> MissingPaperNames = Meter.CreateCounter<long>(
        "ookigrader.template_generation.missing_paper_names",
        unit: "{unit}",
        description: "Extracted units without a readable paper test name.");

    private static readonly Counter<long> MissingGrades = Meter.CreateCounter<long>(
        "ookigrader.template_generation.missing_grades",
        unit: "{unit}",
        description: "Extracted units requiring a user-selected grade.");

    private static readonly Counter<long> GradeConflicts = Meter.CreateCounter<long>(
        "ookigrader.template_generation.grade_conflicts",
        unit: "{unit}",
        description: "Extracted units with conflicting filename and paper grade evidence.");

    private static readonly Counter<long> StepNameMismatches = Meter.CreateCounter<long>(
        "ookigrader.template_generation.step_name_mismatches",
        unit: "{unit}",
        description: "STEP units in a set whose printed base names do not agree.");

    private static readonly Histogram<double> FinalCheckDuration =
        Meter.CreateHistogram<double>(
            "ookigrader.template_generation.final_check_duration",
            unit: "s",
            description: "Time from final-check readiness to atomic confirmation.");

    private static readonly Histogram<long> TemplatesCreated =
        Meter.CreateHistogram<long>(
            "ookigrader.template_generation.templates_created",
            unit: "{template}",
            description: "Number of templates atomically created per confirmed batch.");

    private static readonly Histogram<long> AiCostPerUnit =
        Meter.CreateHistogram<long>(
            "ookigrader.template_generation.ai_cost_per_unit",
            unit: "USD{micro}",
            description: "Settled AI cost for one terminal unit generation run.");

    private static readonly Histogram<long> AiCostPerBatch =
        Meter.CreateHistogram<long>(
            "ookigrader.template_generation.ai_cost_per_batch",
            unit: "USD{micro}",
            description: "Settled AI cost incurred since the preceding terminal batch observation.");

    internal static void BatchCreated(
        TestType testType,
        TemplatePromptSystem promptSystem,
        int profileVersion,
        int unitCount)
    {
        var tags = CommonTags(testType, promptSystem, profileVersion);
        Batches.Add(1, tags);
        PlannedUnits.Record(unitCount, tags);
    }

    internal static void StepPageCountRejected(
        TemplatePromptSystem promptSystem,
        int profileVersion)
    {
        var tags = CommonTags(TestType.Step, promptSystem, profileVersion);
        StepPageCountRejections.Add(1, tags);
    }

    internal static void ExtractionCallDispatched(
        TemplateGenerationProfile profile,
        string provider,
        string model)
    {
        var tags = ProviderTagsOrUnknown(profile, provider, model);
        tags.Add("outcome", "dispatched");
        ExtractionCalls.Add(1, tags);
    }

    internal static void OrientationRetryStarted(
        TemplateGenerationProfile profile,
        string provider,
        string model)
    {
        var tags = ProviderTagsOrUnknown(profile, provider, model);
        OrientationRetries.Add(1, tags);
    }

    internal static void OrientationRetryFailed(
        TemplateGenerationProfile? profile,
        string? provider,
        string? model,
        string errorCode)
    {
        var tags = ProviderTagsOrUnknown(profile, provider, model);
        tags.Add("error_code", Bounded(errorCode, "TEMPLATE_EXTRACTION_FAILED"));
        OrientationRetryFailures.Add(1, tags);
    }

    internal static void UnitExtractionSucceeded(
        TemplateGenerationProfile profile,
        string provider,
        string model,
        long actualUsdMicros)
    {
        var tags = ProviderTagsOrUnknown(profile, provider, model);
        tags.Add("outcome", "succeeded");
        UnitExtractionSuccesses.Add(1, tags);
        AiCostPerUnit.Record(Math.Max(0, actualUsdMicros), tags);
    }

    internal static void UnitExtractionFailed(
        TemplateGenerationProfile? profile,
        string? provider,
        string? model,
        string errorCode,
        long actualUsdMicros)
    {
        var tags = ProviderTagsOrUnknown(profile, provider, model);
        tags.Add("error_code", Bounded(errorCode, "TEMPLATE_EXTRACTION_FAILED"));
        tags.Add("outcome", "failed");
        UnitExtractionFailures.Add(1, tags);
        AiCostPerUnit.Record(Math.Max(0, actualUsdMicros), tags);
    }

    internal static void UnitExtractionCancelled(
        TestType testType,
        TemplatePromptSystem promptSystem,
        int profileVersion,
        string? provider,
        string? model,
        long actualUsdMicros)
    {
        var tags = ProviderTagsOrUnknown(
            testType,
            promptSystem,
            profileVersion,
            provider,
            model);
        tags.Add("outcome", "cancelled");
        AiCostPerUnit.Record(Math.Max(0, actualUsdMicros), tags);
    }

    internal static void MissingPaperName(TemplateGenerationProfile profile) =>
        MissingPaperNames.Add(
            1,
            CommonTags(profile.TestType, profile.PromptSystem, profile.ProfileVersion));

    internal static void MissingGrade(TemplateGenerationProfile profile) =>
        MissingGrades.Add(
            1,
            CommonTags(profile.TestType, profile.PromptSystem, profile.ProfileVersion));

    internal static void GradeConflict(TemplateGenerationProfile profile) =>
        GradeConflicts.Add(
            1,
            CommonTags(profile.TestType, profile.PromptSystem, profile.ProfileVersion));

    internal static void StepNameMismatch(
        TemplatePromptSystem promptSystem,
        int profileVersion) =>
        StepNameMismatches.Add(
            1,
            CommonTags(TestType.Step, promptSystem, profileVersion));

    internal static void BatchConfirmed(
        TestType testType,
        TemplatePromptSystem promptSystem,
        int profileVersion,
        TimeSpan finalCheckDuration,
        int templateCount,
        long actualUsdMicros)
    {
        var tags = CommonTags(testType, promptSystem, profileVersion);
        tags.Add("outcome", "succeeded");
        FinalCheckDuration.Record(
            Math.Max(0, finalCheckDuration.TotalSeconds),
            tags);
        TemplatesCreated.Record(templateCount, tags);
        AiCostPerBatch.Record(Math.Max(0, actualUsdMicros), tags);
    }

    internal static void BatchTerminated(
        TestType testType,
        TemplatePromptSystem promptSystem,
        int profileVersion,
        string outcome,
        long actualUsdMicros)
    {
        var tags = CommonTags(testType, promptSystem, profileVersion);
        tags.Add("outcome", outcome switch
        {
            "failed" => "failed",
            "cancelled" => "cancelled",
            _ => "unknown",
        });
        AiCostPerBatch.Record(Math.Max(0, actualUsdMicros), tags);
    }

    private static TagList ProviderTags(
        TemplateGenerationProfile profile,
        string provider,
        string model) =>
        ProviderTagsOrUnknown(
            (TemplateGenerationProfile?)profile,
            provider,
            model);

    private static TagList ProviderTagsOrUnknown(
        TemplateGenerationProfile? profile,
        string? provider,
        string? model)
    {
        var tags = profile is null
            ? CommonTags(
                (TestType)0,
                (TemplatePromptSystem)0,
                profileVersion: 0)
            : CommonTags(
                profile.TestType,
                profile.PromptSystem,
                profile.ProfileVersion);
        tags.Add("provider", Bounded(provider, "unknown"));
        tags.Add("model", Bounded(model, "unknown"));
        return tags;
    }

    private static TagList ProviderTagsOrUnknown(
        TestType testType,
        TemplatePromptSystem promptSystem,
        int profileVersion,
        string? provider,
        string? model)
    {
        var tags = CommonTags(testType, promptSystem, profileVersion);
        tags.Add("provider", Bounded(provider, "unknown"));
        tags.Add("model", Bounded(model, "unknown"));
        return tags;
    }

    private static TagList CommonTags(
        TestType testType,
        TemplatePromptSystem promptSystem,
        int profileVersion) =>
        new()
        {
            { "test_type", TestTypeTag(testType) },
            { "prompt_system", PromptSystemTag(promptSystem) },
            { "profile_version", profileVersion },
        };

    private static string TestTypeTag(TestType value) => value switch
    {
        TestType.Hop => "hop",
        TestType.Step => "step",
        TestType.ClassPlacement => "class_placement",
        TestType.Other => "other",
        _ => "unknown",
    };

    private static string PromptSystemTag(TemplatePromptSystem value) => value switch
    {
        TemplatePromptSystem.Standard => "standard",
        TemplatePromptSystem.ClassPlacement => "class_placement",
        TemplatePromptSystem.FillBlank => "fill_blank",
        _ => "unknown",
    };

    private static string Bounded(string? value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim();
        return normalized.Length <= 100 ? normalized : normalized[..100];
    }
}
