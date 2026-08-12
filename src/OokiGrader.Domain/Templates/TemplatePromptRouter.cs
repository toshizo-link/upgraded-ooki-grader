using OokiGrader.Domain.Common;

namespace OokiGrader.Domain.Templates;

public static class TemplatePromptRouter
{
    public static TemplatePromptSystem Resolve(
        TestType testType,
        AnswerStyle? answerStyle)
    {
        if (!Enum.IsDefined(testType))
        {
            throw new ArgumentOutOfRangeException(nameof(testType));
        }

        if (testType is not TestType.Other && answerStyle is not null)
        {
            throw Validation(
                "ANSWER_STYLE_NOT_ALLOWED",
                "Answer style is only allowed for Other.",
                "answerStyle");
        }

        return testType switch
        {
            TestType.Hop or TestType.Step => TemplatePromptSystem.Standard,
            TestType.ClassPlacement => TemplatePromptSystem.ClassPlacement,
            TestType.Other when answerStyle is AnswerStyle.Normal =>
                TemplatePromptSystem.Standard,
            TestType.Other when answerStyle is AnswerStyle.FillBlank =>
                TemplatePromptSystem.FillBlank,
            TestType.Other when answerStyle is null => throw Validation(
                "ANSWER_STYLE_REQUIRED",
                "Answer style is required for Other.",
                "answerStyle"),
            TestType.Other => throw Validation(
                "ANSWER_STYLE_INVALID",
                "Answer style is not supported.",
                "answerStyle"),
            _ => throw new ArgumentOutOfRangeException(nameof(testType)),
        };
    }

    private static DomainValidationException Validation(
        string code,
        string message,
        string path) =>
        new([new DomainError(code, message, path)]);
}
