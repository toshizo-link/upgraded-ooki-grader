using OokiGrader.Application.Grading;

namespace OokiGrader.Application.Tests;

public sealed class GradingAccuracyEvaluatorTests
{
    [Fact]
    public void EvaluateReportsSafeAutomaticRangeAndRiskReviewCoverage()
    {
        var report = GradingAccuracyEvaluator.Evaluate(
        [
            Sample("multi-1", "multiple_answers", "correct", 1_000, "correct", 1_000, false),
            Sample("multi-2", "multiple_answers", "correct", 1_000, "correct", 1_000, false),
            Sample("kanji-1", "kanji_required", "correct", 1_000, "correct", 1_000, false),
            Sample("kanji-2", "kanji_required", "incorrect", 0, "incorrect", 0, true),
            Sample("partial-1", "partial_credit", "partial", 500, "partial", 500, true),
            Sample("wrong-1", "incorrect", "incorrect", 0, "incorrect", 0, true),
            Sample("blank-1", "blank", "blank", 0, "blank", 0, true),
        ]);

        Assert.Equal(7, report.Overall.TotalCases);
        Assert.Equal(7, report.Overall.ExactAgreementCount);
        Assert.Equal(10_000, report.Overall.ExactAgreementBasisPoints);
        Assert.Equal(3, report.Overall.AutomaticDecisionCount);
        Assert.Equal(3, report.Overall.SafeAutomaticDecisionCount);
        Assert.Equal(10_000, report.Overall.AutomaticPrecisionBasisPoints);
        Assert.Equal(0, report.Overall.UnsafeAutomaticDecisionCount);
        Assert.Equal(0, report.Overall.IncorrectCreditFalsePositiveCount);
        Assert.Equal(4, report.Overall.RiskCaseCount);
        Assert.Equal(4, report.Overall.RiskCaseReviewCount);
        Assert.Equal(10_000, report.Overall.RiskReviewCoverageBasisPoints);
        Assert.Collection(
            report.Categories,
            item => Assert.Equal("blank", item.Category),
            item => Assert.Equal("incorrect", item.Category),
            item => Assert.Equal("kanji_required", item.Category),
            item => Assert.Equal("multiple_answers", item.Category),
            item => Assert.Equal("partial_credit", item.Category));
    }

    [Fact]
    public void EvaluateExposesIncorrectCreditThatEscapedReview()
    {
        var report = GradingAccuracyEvaluator.Evaluate(
        [
            Sample(
                "wrong-credited",
                "incorrect",
                "incorrect",
                0,
                "correct",
                1_000,
                false),
        ]);

        Assert.Equal(0, report.Overall.ExactAgreementCount);
        Assert.Equal(1, report.Overall.AutomaticDecisionCount);
        Assert.Equal(0, report.Overall.SafeAutomaticDecisionCount);
        Assert.Equal(0, report.Overall.AutomaticPrecisionBasisPoints);
        Assert.Equal(1, report.Overall.UnsafeAutomaticDecisionCount);
        Assert.Equal(1, report.Overall.IncorrectCreditFalsePositiveCount);
        Assert.Equal(1, report.Overall.RiskCaseCount);
        Assert.Equal(0, report.Overall.RiskCaseReviewCount);
        Assert.Equal(0, report.Overall.RiskReviewCoverageBasisPoints);
    }

    [Fact]
    public void EvaluateRejectsDuplicateCaseIdentifiers()
    {
        var samples = new[]
        {
            Sample("duplicate", "blank", "blank", 0, "blank", 0, true),
            Sample("duplicate", "blank", "blank", 0, "blank", 0, true),
        };

        var error = Assert.Throws<ArgumentException>(
            () => GradingAccuracyEvaluator.Evaluate(samples));

        Assert.Equal("samples", error.ParamName);
    }

    private static GradingAccuracySample Sample(
        string caseId,
        string category,
        string expectedOutcome,
        long expectedPointsMilli,
        string actualOutcome,
        long actualPointsMilli,
        bool requiresTeacherReview) =>
        new(
            caseId,
            category,
            expectedOutcome,
            expectedPointsMilli,
            actualOutcome,
            actualPointsMilli,
            requiresTeacherReview);
}
