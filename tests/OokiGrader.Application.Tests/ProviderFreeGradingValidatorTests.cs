using OokiGrader.Application.Grading;

namespace OokiGrader.Application.Tests;

public sealed class ProviderFreeGradingValidatorTests
{
    [Fact]
    public void ValidateComputesExactIntegerTotals()
    {
        var questions = new[]
        {
            new QuestionDefinition("q1", 2_000),
            new QuestionDefinition("q2", 3_000)
        };
        var judgments = new[]
        {
            new QuestionJudgment("q1", 1_000, "partial", "deterministic", 10_000),
            new QuestionJudgment("q2", 3_000, "correct", "manual", 10_000)
        };

        var totals = ProviderFreeGradingValidator.Validate(questions, judgments);

        Assert.Equal(4_000, totals.EarnedPointsMilli);
        Assert.Equal(5_000, totals.PossiblePointsMilli);
    }

    [Fact]
    public void ValidateRejectsMissingAndAboveMaximumJudgments()
    {
        var questions = new[]
        {
            new QuestionDefinition("q1", 1_000),
            new QuestionDefinition("q2", 1_000)
        };

        Assert.Throws<ArgumentException>(() =>
            ProviderFreeGradingValidator.Validate(
                questions,
                [new QuestionJudgment("q1", 1_000, "correct", "manual", 10_000)]));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProviderFreeGradingValidator.Validate(
                [questions[0]],
                [new QuestionJudgment("q1", 1_001, "correct", "manual", 10_000)]));
    }
}
