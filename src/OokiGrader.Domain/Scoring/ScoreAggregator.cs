using System.Collections.ObjectModel;
using OokiGrader.Domain.Common;
using OokiGrader.Domain.Grading;
using OokiGrader.Domain.Templates;

namespace OokiGrader.Domain.Scoring;

public sealed class ScoreSummary
{
    private readonly ReadOnlyCollection<QuestionGradeResult> _results;

    internal ScoreSummary(
        MilliPoints awardedPoints,
        MilliPoints maximumPoints,
        IEnumerable<QuestionGradeResult> results,
        int reviewRequiredCount)
    {
        AwardedPoints = awardedPoints;
        MaximumPoints = maximumPoints;
        _results = Array.AsReadOnly(results.ToArray());
        ReviewRequiredCount = reviewRequiredCount;
    }

    public MilliPoints AwardedPoints { get; }

    public MilliPoints MaximumPoints { get; }

    public IReadOnlyList<QuestionGradeResult> Results => _results;

    public int ReviewRequiredCount { get; }

    public bool CanFinalize => ReviewRequiredCount == 0;

    public int PercentageBasisPoints =>
        MaximumPoints == MilliPoints.Zero
            ? 0
            : checked((int)decimal.Round(
                AwardedPoints.Value * 10_000m / MaximumPoints.Value,
                0,
                MidpointRounding.AwayFromZero));
}

public static class ScoreAggregator
{
    public static ScoreSummary Aggregate(
        IEnumerable<QuestionDefinition> questions,
        IEnumerable<QuestionGradeResult> results)
    {
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(results);

        var questionArray = questions.ToArray();
        var resultArray = results.ToArray();
        var errors = new List<DomainError>();

        AddDuplicateErrors(
            questionArray.Select(question => question.Id),
            errors,
            "score.duplicate_question",
            "Question IDs must be unique.");
        AddDuplicateErrors(
            resultArray.Select(result => result.QuestionId),
            errors,
            "score.duplicate_result",
            "There must be exactly one result per question.");

        var questionById = questionArray
            .GroupBy(question => question.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var resultById = resultArray
            .GroupBy(result => result.QuestionId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var question in questionArray)
        {
            if (!resultById.ContainsKey(question.Id))
            {
                errors.Add(
                    new DomainError(
                        "score.missing_result",
                        $"Question '{question.Id}' has no result.",
                        question.Id));
            }
        }

        foreach (var result in resultArray)
        {
            if (!questionById.TryGetValue(result.QuestionId, out var question))
            {
                errors.Add(
                    new DomainError(
                        "score.unknown_question",
                        $"Result references unknown question '{result.QuestionId}'.",
                        result.QuestionId));
                continue;
            }

            if (result.MaximumPoints != question.MaximumPoints)
            {
                errors.Add(
                    new DomainError(
                        "score.maximum_mismatch",
                        "The result maximum does not match the published question snapshot.",
                        result.QuestionId));
            }

            var awardValidation = question.PointPolicy.ValidateAward(
                result.AwardedPoints,
                result.QuestionId);
            errors.AddRange(awardValidation.Errors);
        }

        if (errors.Count > 0)
        {
            throw new DomainValidationException(errors);
        }

        try
        {
            var awarded = resultArray.Aggregate(
                MilliPoints.Zero,
                (total, result) => total + result.AwardedPoints);
            var maximum = questionArray.Aggregate(
                MilliPoints.Zero,
                (total, question) => total + question.MaximumPoints);

            return new ScoreSummary(
                awarded,
                maximum,
                resultArray.OrderBy(
                    result => questionById[result.QuestionId].OrderIndex),
                resultArray.Count(result => result.RequiresReview));
        }
        catch (OverflowException exception)
        {
            throw new DomainValidationException(
            [
                new DomainError(
                    "score.total_overflow",
                    $"Score totals exceed the supported 64-bit range: {exception.Message}"),
            ]);
        }
    }

    private static void AddDuplicateErrors(
        IEnumerable<string> values,
        List<DomainError> errors,
        string code,
        string message)
    {
        if (values.GroupBy(value => value, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            errors.Add(new DomainError(code, message));
        }
    }
}
