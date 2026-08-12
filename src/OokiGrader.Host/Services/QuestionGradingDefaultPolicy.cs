using OokiGrader.Domain.Templates;

namespace OokiGrader.Host.Services;

internal static class QuestionGradingDefaultPolicy
{
    private const int MaximumAnswerExcerptLength = 1_000;
    private const long OnePointMilli = 1_000;

    public static string GradingModeFor(string questionType) =>
        ToPersistenceValue(QuestionGradingDefaults.For(ParseQuestionType(questionType)));

    public static bool RequiresReviewAlwaysFor(string questionType) =>
        QuestionGradingDefaults.RequiresReviewAlwaysByDefault(
            ParseQuestionType(questionType));

    public static long PointIncrementMilliFor(long maximumPointsMilli)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPointsMilli);
        return GreatestCommonDivisor(maximumPointsMilli, OnePointMilli);
    }

    public static string? BuildDefaultRubric(
        string questionType,
        string? expectedAnswer)
    {
        if (QuestionGradingDefaults.For(ParseQuestionType(questionType))
            != GradingMode.AiRubric)
        {
            return null;
        }

        var trimmedAnswer = string.IsNullOrWhiteSpace(expectedAnswer)
            ? null
            : expectedAnswer.Trim();
        if (trimmedAnswer?.Length > MaximumAnswerExcerptLength)
        {
            trimmedAnswer = $"{trimmedAnswer[..MaximumAnswerExcerptLength]}…";
        }

        var comparisonRule = trimmedAnswer is null
            ? "問題文に対して内容が正しく、必要な説明要素を満たしているかを確認する。"
            : $"模範解答「{trimmedAnswer}」と意味および必要な説明要素が一致しているかを確認する。";
        return comparisonRule
            + "十分に満たす場合は満点を提案し、部分的に満たす場合は配点刻みの範囲で部分点を提案する。"
            + "曖昧、判読困難、または採点基準だけでは判断できない場合は、点数を確定せず先生の確認に回す。";
    }

    private static QuestionType ParseQuestionType(string value) =>
        value switch
        {
            "multiple_choice" => QuestionType.MultipleChoice,
            "boolean" => QuestionType.Boolean,
            "numeric" => QuestionType.Numeric,
            "exact_short_text" => QuestionType.ExactShortText,
            "semantic_short_text" => QuestionType.SemanticShortText,
            "multi_part" => QuestionType.MultiPart,
            "subjective" => QuestionType.Subjective,
            "unsupported" => QuestionType.Unsupported,
            _ => throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "Unsupported question type."),
        };

    private static string ToPersistenceValue(GradingMode value) =>
        value switch
        {
            GradingMode.Deterministic => "deterministic",
            GradingMode.TranscribeThenRules => "transcribe_then_rules",
            GradingMode.AiRubric => "ai_rubric",
            GradingMode.Manual => "manual",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private static long GreatestCommonDivisor(long left, long right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return left;
    }
}
