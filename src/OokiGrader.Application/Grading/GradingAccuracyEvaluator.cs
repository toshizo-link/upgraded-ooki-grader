namespace OokiGrader.Application.Grading;

public sealed record GradingAccuracySample(
    string CaseId,
    string Category,
    string ExpectedOutcome,
    long ExpectedPointsMilli,
    string ActualOutcome,
    long ActualPointsMilli,
    bool RequiresTeacherReview);

public sealed record GradingAccuracyMetrics(
    int TotalCases,
    int ExactAgreementCount,
    int ExactAgreementBasisPoints,
    int AutomaticDecisionCount,
    int SafeAutomaticDecisionCount,
    int? AutomaticPrecisionBasisPoints,
    int UnsafeAutomaticDecisionCount,
    int IncorrectCreditFalsePositiveCount,
    int RiskCaseCount,
    int RiskCaseReviewCount,
    int? RiskReviewCoverageBasisPoints);

public sealed record GradingAccuracyCategoryMetrics(
    string Category,
    GradingAccuracyMetrics Metrics);

public sealed record GradingAccuracyReport(
    GradingAccuracyMetrics Overall,
    IReadOnlyList<GradingAccuracyCategoryMetrics> Categories);

public static class GradingAccuracyEvaluator
{
    private static readonly HashSet<string> AllowedOutcomes =
    [
        "correct",
        "incorrect",
        "partial",
        "blank",
        "unreadable",
        "review",
    ];

    public static GradingAccuracyReport Evaluate(
        IEnumerable<GradingAccuracySample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var materialized = samples.ToArray();
        if (materialized.Length == 0
            || materialized.Any(sample => !IsValid(sample))
            || materialized
                .Select(sample => sample.CaseId)
                .Distinct(StringComparer.Ordinal)
                .Count() != materialized.Length)
        {
            throw new ArgumentException(
                "Accuracy samples must be valid and have unique case identifiers.",
                nameof(samples));
        }

        var categories = materialized
            .GroupBy(sample => sample.Category, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new GradingAccuracyCategoryMetrics(
                group.Key,
                Calculate(group)))
            .ToArray();
        return new GradingAccuracyReport(
            Calculate(materialized),
            categories);
    }

    private static bool IsValid(GradingAccuracySample sample) =>
        !string.IsNullOrWhiteSpace(sample.CaseId)
        && !string.IsNullOrWhiteSpace(sample.Category)
        && AllowedOutcomes.Contains(sample.ExpectedOutcome)
        && AllowedOutcomes.Contains(sample.ActualOutcome)
        && sample.ExpectedPointsMilli >= 0
        && sample.ActualPointsMilli >= 0;

    private static GradingAccuracyMetrics Calculate(
        IEnumerable<GradingAccuracySample> samples)
    {
        var materialized = samples.ToArray();
        var exactAgreementCount = materialized.Count(IsExactAgreement);
        var automatic = materialized
            .Where(sample => !sample.RequiresTeacherReview)
            .ToArray();
        var safeAutomaticCount = automatic.Count(sample =>
            IsExactAgreement(sample)
            && sample.ExpectedOutcome == "correct"
            && sample.ActualOutcome == "correct");
        var risk = materialized
            .Where(sample => !IsExactAgreement(sample)
                || sample.ExpectedOutcome != "correct"
                || sample.ActualOutcome != "correct")
            .ToArray();
        var riskReviewCount = risk.Count(sample =>
            sample.RequiresTeacherReview);

        return new GradingAccuracyMetrics(
            materialized.Length,
            exactAgreementCount,
            BasisPoints(exactAgreementCount, materialized.Length)!.Value,
            automatic.Length,
            safeAutomaticCount,
            BasisPoints(safeAutomaticCount, automatic.Length),
            automatic.Length - safeAutomaticCount,
            materialized.Count(sample =>
                sample.ActualPointsMilli > sample.ExpectedPointsMilli),
            risk.Length,
            riskReviewCount,
            BasisPoints(riskReviewCount, risk.Length));
    }

    private static bool IsExactAgreement(GradingAccuracySample sample) =>
        sample.ExpectedOutcome == sample.ActualOutcome
        && sample.ExpectedPointsMilli == sample.ActualPointsMilli;

    private static int? BasisPoints(int numerator, int denominator) =>
        denominator == 0
            ? null
            : checked((int)Math.Round(
                numerator * 10_000d / denominator,
                MidpointRounding.AwayFromZero));
}
