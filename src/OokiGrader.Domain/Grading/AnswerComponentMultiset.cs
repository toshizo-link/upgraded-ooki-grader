using System.Text;

namespace OokiGrader.Domain.Grading;

/// <summary>
/// Deterministic component parsing for teacher-enabled order-insensitive
/// answers. Only explicit list separators are recognized; ordinary spaces are
/// preserved inside an answer component.
/// </summary>
public static class AnswerComponentMultiset
{
    public const string SupportedSeparatorsDescription =
        "、, ， / ／ ; ； ・ または改行";

    public static bool Equals(string? left, string? right)
    {
        var leftComponents = Parse(left);
        var rightComponents = Parse(right);
        return leftComponents.Count == rightComponents.Count
            && leftComponents
                .Order(StringComparer.Ordinal)
                .SequenceEqual(
                    rightComponents.Order(StringComparer.Ordinal),
                    StringComparer.Ordinal);
    }

    public static IReadOnlyList<string> Parse(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return [];
        }

        var normalizedWidth = value.Normalize(NormalizationForm.FormKC);
        var components = new List<string>();
        var current = new StringBuilder(normalizedWidth.Length);

        foreach (var character in normalizedWidth)
        {
            if (IsSeparator(character))
            {
                AddComponent(components, current);
                continue;
            }

            current.Append(character);
        }

        AddComponent(components, current);
        return components;
    }

    private static bool IsSeparator(char value) =>
        value is '、' or ',' or '/' or ';' or '・' or '\r' or '\n';

    private static void AddComponent(
        List<string> destination,
        StringBuilder current)
    {
        var component = JapaneseTextNormalizer.NormalizeForComparison(
            current.ToString());
        current.Clear();
        if (component.Length > 0)
        {
            destination.Add(component);
        }
    }
}
