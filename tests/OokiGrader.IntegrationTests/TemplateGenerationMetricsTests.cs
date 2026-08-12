using System.Diagnostics.Metrics;
using OokiGrader.Domain.Templates;
using OokiGrader.Host.Observability;

namespace OokiGrader.IntegrationTests;

public sealed class TemplateGenerationMetricsTests
{
    private static readonly string[] CommonTagKeys =
        ["profile_version", "prompt_system", "test_type"];

    [Fact]
    public void PlanningAndRejectionMetricsUseOnlyBoundedDimensions()
    {
        using var collector = new MetricCollector();

        TemplateGenerationMetrics.BatchCreated(
            TestType.Step,
            TemplatePromptSystem.Standard,
            TemplateGenerationProfile.CurrentProfileVersion,
            unitCount: 6);
        TemplateGenerationMetrics.StepPageCountRejected(
            TemplatePromptSystem.Standard,
            TemplateGenerationProfile.CurrentProfileVersion);

        var batch = collector.Single(
            "ookigrader.template_generation.batches");
        Assert.Equal(1L, batch.LongValue);
        Assert.Equal("step", batch.Tags["test_type"]);
        Assert.Equal("standard", batch.Tags["prompt_system"]);
        Assert.Equal(1, batch.Tags["profile_version"]);

        var planned = collector.Single(
            "ookigrader.template_generation.planned_units");
        Assert.Equal(6L, planned.LongValue);
        Assert.Equal(
            CommonTagKeys,
            planned.Tags.Keys.Order(StringComparer.Ordinal));

        var rejection = collector.Single(
            "ookigrader.template_generation.step_page_count_rejections");
        Assert.Equal(1L, rejection.LongValue);
        Assert.Equal("step", rejection.Tags["test_type"]);
    }

    [Fact]
    public void UnitMetricsBoundProviderModelAndErrorTagsWithoutContentLabels()
    {
        using var collector = new MetricCollector();
        var profile = Profile(TestType.Hop, TemplatePromptSystem.Standard);
        var oversizedProvider = new string('p', 120);
        var oversizedModel = new string('m', 120);
        var oversizedError = new string('e', 120);

        TemplateGenerationMetrics.ExtractionCallDispatched(
            profile,
            oversizedProvider,
            oversizedModel);
        TemplateGenerationMetrics.OrientationRetryStarted(
            profile,
            oversizedProvider,
            oversizedModel);
        TemplateGenerationMetrics.OrientationRetryFailed(
            profile,
            oversizedProvider,
            oversizedModel,
            oversizedError);
        TemplateGenerationMetrics.UnitExtractionSucceeded(
            profile,
            oversizedProvider,
            oversizedModel,
            actualUsdMicros: 1_250);
        TemplateGenerationMetrics.UnitExtractionFailed(
            profile,
            oversizedProvider,
            oversizedModel,
            oversizedError,
            actualUsdMicros: 625);
        TemplateGenerationMetrics.MissingPaperName(profile);
        TemplateGenerationMetrics.MissingGrade(profile);
        TemplateGenerationMetrics.GradeConflict(profile);

        var call = collector.Single(
            "ookigrader.template_generation.extraction_calls");
        Assert.Equal(100, Assert.IsType<string>(call.Tags["provider"]).Length);
        Assert.Equal(100, Assert.IsType<string>(call.Tags["model"]).Length);
        Assert.Equal("dispatched", call.Tags["outcome"]);

        var failure = collector.Single(
            "ookigrader.template_generation.unit_extraction_failures");
        Assert.Equal(
            100,
            Assert.IsType<string>(failure.Tags["error_code"]).Length);
        Assert.DoesNotContain("subject", failure.Tags.Keys);
        Assert.DoesNotContain("batch_id", failure.Tags.Keys);
        Assert.DoesNotContain("unit_id", failure.Tags.Keys);
        Assert.DoesNotContain("filename", failure.Tags.Keys);
        Assert.DoesNotContain("name", failure.Tags.Keys);

        Assert.Equal(
            1L,
            collector.Single(
                "ookigrader.template_generation.unit_extraction_successes").LongValue);
        var unitCosts = collector.All(
            "ookigrader.template_generation.ai_cost_per_unit");
        Assert.Equal(2, unitCosts.Length);
        Assert.Equal(
            1_250L,
            Assert.Single(
                unitCosts,
                item => Equals(item.Tags["outcome"], "succeeded")).LongValue);
        Assert.Equal(
            625L,
            Assert.Single(
                unitCosts,
                item => Equals(item.Tags["outcome"], "failed")).LongValue);
        Assert.Equal(
            1L,
            collector.Single(
                "ookigrader.template_generation.orientation_retries").LongValue);
        Assert.Equal(
            1L,
            collector.Single(
                "ookigrader.template_generation.orientation_retry_failures").LongValue);
        Assert.Equal(
            1L,
            collector.Single(
                "ookigrader.template_generation.missing_paper_names").LongValue);
        Assert.Equal(
            1L,
            collector.Single(
                "ookigrader.template_generation.missing_grades").LongValue);
        Assert.Equal(
            1L,
            collector.Single(
                "ookigrader.template_generation.grade_conflicts").LongValue);
    }

    [Fact]
    public void CompletionMetricsRecordDurationAndTemplatesPerBatch()
    {
        using var collector = new MetricCollector();

        TemplateGenerationMetrics.StepNameMismatch(
            TemplatePromptSystem.Standard,
            TemplateGenerationProfile.CurrentProfileVersion);
        TemplateGenerationMetrics.BatchConfirmed(
            TestType.Step,
            TemplatePromptSystem.Standard,
            TemplateGenerationProfile.CurrentProfileVersion,
            TimeSpan.FromSeconds(42.5),
            templateCount: 3,
            actualUsdMicros: 3_750);
        TemplateGenerationMetrics.BatchTerminated(
            TestType.Step,
            TemplatePromptSystem.Standard,
            TemplateGenerationProfile.CurrentProfileVersion,
            "failed",
            actualUsdMicros: 2_000);
        TemplateGenerationMetrics.BatchTerminated(
            TestType.Step,
            TemplatePromptSystem.Standard,
            TemplateGenerationProfile.CurrentProfileVersion,
            "cancelled",
            actualUsdMicros: 900);

        var duration = collector.Single(
            "ookigrader.template_generation.final_check_duration");
        Assert.Equal(42.5, duration.DoubleValue);
        Assert.Equal("succeeded", duration.Tags["outcome"]);
        Assert.Equal(
            3L,
            collector.Single(
                "ookigrader.template_generation.templates_created").LongValue);
        var batchCosts = collector.All(
            "ookigrader.template_generation.ai_cost_per_batch");
        Assert.Equal(3, batchCosts.Length);
        Assert.Equal(
            3_750L,
            Assert.Single(
                batchCosts,
                item => Equals(item.Tags["outcome"], "succeeded")).LongValue);
        Assert.Equal(
            2_000L,
            Assert.Single(
                batchCosts,
                item => Equals(item.Tags["outcome"], "failed")).LongValue);
        Assert.Equal(
            900L,
            Assert.Single(
                batchCosts,
                item => Equals(item.Tags["outcome"], "cancelled")).LongValue);
        Assert.Equal(
            1L,
            collector.Single(
                "ookigrader.template_generation.step_name_mismatches").LongValue);
    }

    private static TemplateGenerationProfile Profile(
        TestType testType,
        TemplatePromptSystem promptSystem) =>
        new(
            TemplateGenerationProfile.CurrentProfileVersion,
            testType,
            "算数",
            AnswerStyle: null,
            promptSystem,
            SourcePageCount: 1,
            UnitSequence: 1,
            FirstPage: 1,
            LastPage: 1,
            StepSetIndex: null,
            StepVariationIndex: null,
            DeterministicSuffix: null,
            TemplateGenerationProfile.CurrentSplitPolicyVersion,
            TemplateGenerationProfile.CurrentNamingPolicyVersion,
            "template-extract-v2.0.0",
            "template_extract_v5");

    private sealed class MetricCollector : IDisposable
    {
        private readonly object _gate = new();
        private readonly List<Measurement> _measurements = [];
        private readonly MeterListener _listener = new();

        public MetricCollector()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == TemplateGenerationMetrics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>(
                (instrument, measurement, tags, _) =>
                    Add(instrument, measurement, null, tags));
            _listener.SetMeasurementEventCallback<double>(
                (instrument, measurement, tags, _) =>
                    Add(instrument, null, measurement, tags));
            _listener.Start();
        }

        public Measurement Single(string instrumentName)
        {
            lock (_gate)
            {
                return Assert.Single(
                    _measurements,
                    item => item.InstrumentName == instrumentName);
            }
        }

        public Measurement[] All(string instrumentName)
        {
            lock (_gate)
            {
                return _measurements
                    .Where(item => item.InstrumentName == instrumentName)
                    .ToArray();
            }
        }

        public void Dispose() => _listener.Dispose();

        private void Add(
            Instrument instrument,
            long? longValue,
            double? doubleValue,
            ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var copiedTags = tags.ToArray().ToDictionary(
                item => item.Key,
                item => item.Value,
                StringComparer.Ordinal);
            lock (_gate)
            {
                _measurements.Add(new Measurement(
                    instrument.Name,
                    longValue,
                    doubleValue,
                    copiedTags));
            }
        }
    }

    private sealed record Measurement(
        string InstrumentName,
        long? LongValue,
        double? DoubleValue,
        IReadOnlyDictionary<string, object?> Tags);
}
