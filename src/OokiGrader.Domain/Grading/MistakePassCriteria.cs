namespace OokiGrader.Domain.Grading;

public static class MistakePassCriteria
{
    // Each scored question is one mistake unless fully correct; partial,
    // blank and unreadable answers count as mistakes too.
    public static int CountMistakes(IEnumerable<string> outcomes) =>
        outcomes.Count(outcome => outcome != "correct");

    public static string Evaluate(int? maximumMistakes, IEnumerable<string> outcomes) =>
        Evaluate(maximumMistakes, CountMistakes(outcomes));

    public static string Evaluate(int? maximumMistakes, int mistakeCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumMistakes ?? 0);
        ArgumentOutOfRangeException.ThrowIfNegative(mistakeCount);
        return maximumMistakes is null ? "unconfigured"
            : mistakeCount <= maximumMistakes ? "pass" : "fail";
    }
}
