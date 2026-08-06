using OokiGrader.Domain.Common;
using OokiGrader.Domain.Grading;
using OokiGrader.Domain.Scoring;

namespace OokiGrader.Domain.Tests;

public sealed class ScoreAggregatorTests
{
    [Fact]
    public void AggregatesIntegerMilliPointTotals()
    {
        var first = TestQuestionFactory.ExactText(
            id: "q-1",
            orderIndex: 0,
            displayLabel: "問1",
            maximum: new MilliPoints(2000),
            increment: new MilliPoints(1000));
        var second = TestQuestionFactory.ExactText(
            id: "q-2",
            orderIndex: 1,
            displayLabel: "問2");
        var results = new[]
        {
            DeterministicGrader.Grade(first, new AnswerObservation("漢字")),
            DeterministicGrader.Grade(second, new AnswerObservation("違う")),
        };

        var summary = ScoreAggregator.Aggregate([first, second], results);

        Assert.Equal(2000, summary.AwardedPoints.Value);
        Assert.Equal(3000, summary.MaximumPoints.Value);
        Assert.Equal(6667, summary.PercentageBasisPoints);
        Assert.True(summary.CanFinalize);
    }

    [Fact]
    public void ReviewRequiredResultBlocksFinalization()
    {
        var question = TestQuestionFactory.ExactText();
        var result = DeterministicGrader.Grade(
            question,
            new AnswerObservation("不明", AnswerQuality.Unreadable));

        var summary = ScoreAggregator.Aggregate([question], [result]);

        Assert.False(summary.CanFinalize);
        Assert.Equal(1, summary.ReviewRequiredCount);
    }

    [Fact]
    public void MissingResultIsRejected()
    {
        var question = TestQuestionFactory.ExactText();

        var exception = Assert.Throws<DomainValidationException>(
            () => ScoreAggregator.Aggregate([question], []));

        Assert.Contains(
            exception.Errors,
            error => error.Code == "score.missing_result");
    }

    [Fact]
    public void DuplicateResultIsRejected()
    {
        var question = TestQuestionFactory.ExactText();
        var result = DeterministicGrader.Grade(
            question,
            new AnswerObservation("漢字"));

        var exception = Assert.Throws<DomainValidationException>(
            () => ScoreAggregator.Aggregate([question], [result, result]));

        Assert.Contains(
            exception.Errors,
            error => error.Code == "score.duplicate_result");
    }

    [Fact]
    public void UnknownQuestionResultIsRejected()
    {
        var expected = TestQuestionFactory.ExactText(id: "expected");
        var foreignQuestion = TestQuestionFactory.ExactText(id: "foreign");
        var foreignResult = DeterministicGrader.Grade(
            foreignQuestion,
            new AnswerObservation("漢字"));

        var exception = Assert.Throws<DomainValidationException>(
            () => ScoreAggregator.Aggregate([expected], [foreignResult]));

        Assert.Contains(
            exception.Errors,
            error => error.Code == "score.unknown_question");
    }

    [Fact]
    public void ResultMaximumMustMatchPublishedQuestionSnapshot()
    {
        var question = TestQuestionFactory.ExactText();
        var forged = new QuestionGradeResult(
            question.Id,
            new MilliPoints(1000),
            new MilliPoints(2000),
            GradeDisposition.Partial,
            GradingStage.Rubric,
            GradeReason.RubricProposal,
            requiresReview: true,
            normalizedTranscription: "漢字");

        var exception = Assert.Throws<DomainValidationException>(
            () => ScoreAggregator.Aggregate([question], [forged]));

        Assert.Contains(
            exception.Errors,
            error => error.Code == "score.maximum_mismatch");
    }
}
