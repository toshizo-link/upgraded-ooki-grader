using OokiGrader.Domain.Scoring;
using OokiGrader.Domain.Templates;

namespace OokiGrader.Domain.Tests;

internal static class TestQuestionFactory
{
    public static QuestionDefinition ExactText(
        string id = "q-1",
        int orderIndex = 0,
        string displayLabel = "問1",
        string canonicalText = "漢字",
        bool allowNonKanji = false,
        bool requiresReviewAlways = false,
        bool teacherVerified = true,
        IEnumerable<AcceptedAnswer>? additionalAnswers = null,
        MilliPoints? maximum = null,
        MilliPoints? increment = null,
        bool requiresCompleteAnswer = false,
        bool answerOrderInsensitive = false)
    {
        var answers = new List<AcceptedAnswer>
        {
            Answer(
                $"{id}-canonical",
                canonicalText,
                AcceptedAnswerVariantType.Canonical),
        };
        if (additionalAnswers is not null)
        {
            answers.AddRange(additionalAnswers);
        }

        return new QuestionDefinition(
            id,
            $"logical-{id}",
            orderIndex,
            displayLabel,
            "次の答えを書きなさい。",
            QuestionType.ExactShortText,
            GradingMode.TranscribeThenRules,
            maximum ?? new MilliPoints(1000),
            increment ?? new MilliPoints(1000),
            allowNonKanji,
            requiresReviewAlways,
            teacherVerified,
            answers,
            requiresCompleteAnswer: requiresCompleteAnswer,
            answerOrderInsensitive: answerOrderInsensitive);
    }

    public static QuestionDefinition Numeric(
        decimal expected,
        NumericFormat format = NumericFormat.Any,
        IEnumerable<string>? units = null,
        bool unitRequired = false,
        decimal? absoluteTolerance = null,
        string id = "q-numeric")
    {
        return new QuestionDefinition(
            id,
            $"logical-{id}",
            0,
            "問数",
            "数値を書きなさい。",
            QuestionType.Numeric,
            GradingMode.TranscribeThenRules,
            new MilliPoints(2000),
            new MilliPoints(1000),
            allowNonKanji: true,
            requiresReviewAlways: false,
            teacherVerified: true,
            acceptedAnswers:
            [
                Answer(
                    $"{id}-canonical",
                    expected.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    AcceptedAnswerVariantType.Canonical),
            ],
            numericPolicy: new NumericAnswerPolicy(
                expected,
                format,
                absoluteTolerance,
                acceptedUnits: units,
                unitRequired: unitRequired));
    }

    public static QuestionDefinition Choice(
        string id = "q-choice",
        string correctChoice = "A",
        IEnumerable<string>? allowedChoices = null)
    {
        var choices = (allowedChoices ?? ["A", "B", "C"]).ToArray();
        return new QuestionDefinition(
            id,
            $"logical-{id}",
            0,
            "問選",
            "選びなさい。",
            QuestionType.MultipleChoice,
            GradingMode.Deterministic,
            new MilliPoints(1000),
            new MilliPoints(1000),
            allowNonKanji: true,
            requiresReviewAlways: false,
            teacherVerified: true,
            acceptedAnswers:
            [
                Answer(
                    $"{id}-canonical",
                    correctChoice,
                    AcceptedAnswerVariantType.Canonical),
            ],
            choicePolicy: new ChoiceAnswerPolicy(correctChoice, choices));
    }

    public static AcceptedAnswer Answer(
        string id,
        string text,
        AcceptedAnswerVariantType type,
        bool teacherVerified = true,
        AnswerProvenance provenance = AnswerProvenance.TeacherEntered,
        AnswerSourceReference? source = null) =>
        new(id, text, type, provenance, teacherVerified, source);
}
