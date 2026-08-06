using System.Text;

namespace OokiGrader.Host.Api;

internal static class TemplateSourceRoleInference
{
    private static readonly string[] EmbeddedAnswerMarkers =
    [
        "模範解答記入済",
        "模範解答付き",
        "模範解答入り",
        "正答付き",
        "正答入り",
        "解答例付き",
        "解答例入り",
        "解答見本付き",
        "解答見本入り",
        "記入例",
        "完成例",
        "modelanswerincluded",
        "modelanswerfilled",
        "answerkeyincluded",
        "answerkeyfilled",
    ];

    private static readonly string[] ExplicitNonModelAnswerMarkers =
    [
        "非模範解答",
        "模範解答ではない",
        "生徒答案",
        "生徒解答",
        "nonmodelanswer",
        "studentanswer",
        "studentresponse",
    ];

    private static readonly string[] NonModelAnswerMarkers =
    [
        "解答付き",
        "解答記入済",
        "答案記入済",
        "記入済",
        "採点済み",
        "completedtest",
        "completedexam",
        "answeredtest",
        "filledtest",
        "filledexam",
    ];

    private static readonly string[] SeparateAnswerMarkers =
    [
        "模範解答",
        "解答例",
        "正答",
        "採点基準",
        "採点表",
        "answerkey",
        "modelanswer",
        "solutions",
    ];

    private static readonly string[] BlankTestMarkers =
    [
        "問題用紙",
        "問題",
        "テスト",
        "試験",
        "プリント",
        "question",
        "exam",
        "test",
    ];

    public static TemplateSourceRoleResolution Infer(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        var normalized = Normalize(displayName);

        // Explicitly non-model/student wording wins even if it contains a
        // substring such as "模範解答" or "modelanswer".
        if (ContainsAny(normalized, ExplicitNonModelAnswerMarkers))
        {
            return new TemplateSourceRoleResolution(
                "contains_non_model_answers",
                9_500,
                "filename_non_model_answers");
        }

        if (ContainsAny(normalized, EmbeddedAnswerMarkers))
        {
            return new TemplateSourceRoleResolution(
                "contains_model_answers",
                9_500,
                "filename_embedded_model_answer");
        }

        // Generic filled/completed wording never grants answer authority.
        if (ContainsAny(normalized, NonModelAnswerMarkers))
        {
            return new TemplateSourceRoleResolution(
                "contains_non_model_answers",
                9_500,
                "filename_non_model_answers");
        }

        if (!normalized.Contains("解答用紙", StringComparison.Ordinal)
            && !normalized.Contains("答案用紙", StringComparison.Ordinal)
            && ContainsAny(normalized, SeparateAnswerMarkers))
        {
            return new TemplateSourceRoleResolution(
                "separate_answer_key",
                9_500,
                "filename_separate_answer_key");
        }

        if (ContainsAny(normalized, BlankTestMarkers)
            || normalized.Contains("解答用紙", StringComparison.Ordinal)
            || normalized.Contains("答案用紙", StringComparison.Ordinal))
        {
            return new TemplateSourceRoleResolution(
                "blank_test",
                9_000,
                "filename_blank_test");
        }

        // Unknown files are deliberately treated as non-authoritative. This
        // can create a review item for a missing answer, but cannot silently
        // elevate arbitrary handwriting into a supplied grading key.
        return new TemplateSourceRoleResolution(
            "blank_test",
            5_000,
            "safe_non_authoritative_fallback");
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value
                     .Normalize(NormalizationForm.FormKC)
                     .ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static bool ContainsAny(
        string value,
        IEnumerable<string> markers) =>
        markers.Any(marker => value.Contains(marker, StringComparison.Ordinal));
}

internal sealed record TemplateSourceRoleResolution(
    string SourceRole,
    int ConfidenceBasisPoints,
    string ReasonCode);
