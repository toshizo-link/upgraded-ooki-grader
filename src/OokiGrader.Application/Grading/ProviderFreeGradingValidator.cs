namespace OokiGrader.Application.Grading;

public static class ProviderFreeGradingValidator
{
    public static ValidatedGradeTotals Validate(
        IReadOnlyCollection<QuestionDefinition> questions,
        IReadOnlyCollection<QuestionJudgment> judgments)
    {
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(judgments);

        if (questions.Count == 0)
        {
            throw new ArgumentException("At least one question is required.", nameof(questions));
        }

        var definitions = new Dictionary<string, QuestionDefinition>(StringComparer.Ordinal);
        foreach (var question in questions)
        {
            if (string.IsNullOrWhiteSpace(question.QuestionId))
            {
                throw new ArgumentException("Question IDs are required.", nameof(questions));
            }

            if (question.MaxPointsMilli < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(questions),
                    "Question maximum points cannot be negative.");
            }

            if (!definitions.TryAdd(question.QuestionId, question))
            {
                throw new ArgumentException(
                    $"Question '{question.QuestionId}' is duplicated.",
                    nameof(questions));
            }
        }

        var judgedIds = new HashSet<string>(StringComparer.Ordinal);
        long earned = 0;

        foreach (var judgment in judgments)
        {
            if (!definitions.TryGetValue(judgment.QuestionId, out var definition))
            {
                throw new ArgumentException(
                    $"Judgment references unknown question '{judgment.QuestionId}'.",
                    nameof(judgments));
            }

            if (!judgedIds.Add(judgment.QuestionId))
            {
                throw new ArgumentException(
                    $"Question '{judgment.QuestionId}' has more than one judgment.",
                    nameof(judgments));
            }

            if (judgment.AwardedPointsMilli < 0 ||
                judgment.AwardedPointsMilli > definition.MaxPointsMilli)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(judgments),
                    $"Awarded points for '{judgment.QuestionId}' are outside its allowed range.");
            }

            if (judgment.ConfidenceBasisPoints is < 0 or > 10_000)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(judgments),
                    $"Confidence for '{judgment.QuestionId}' must be between 0 and 10000.");
            }

            earned = checked(earned + judgment.AwardedPointsMilli);
        }

        if (judgedIds.Count != definitions.Count)
        {
            var missing = definitions.Keys.Except(judgedIds, StringComparer.Ordinal);
            throw new ArgumentException(
                $"Judgments are missing for: {string.Join(", ", missing)}.",
                nameof(judgments));
        }

        var possible = questions.Aggregate(
            0L,
            static (total, question) => checked(total + question.MaxPointsMilli));

        return new ValidatedGradeTotals(earned, possible);
    }
}
