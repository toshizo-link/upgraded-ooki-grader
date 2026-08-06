using System.Globalization;
using System.Text;

namespace OokiGrader.Domain.Grading;

internal static class ChoiceLabelNormalizer
{
    private static readonly HashSet<char> TrailingSeparators =
    [
        '.',
        '。',
        '、',
        ':',
        ';',
        ')',
        ']',
    ];

    private static readonly Dictionary<char, char> Wrappers =
        new Dictionary<char, char>
        {
            ['('] = ')',
            ['['] = ']',
            ['{'] = '}',
            ['<'] = '>',
            ['「'] = '」',
            ['『'] = '』',
            ['【'] = '】',
            ['〔'] = '〕',
            ['〈'] = '〉',
            ['《'] = '》',
        };

    public static bool TryMatchAllowedChoice(
        string normalizedTranscription,
        IReadOnlyList<string> allowedChoices,
        out string matchedChoice)
    {
        ArgumentNullException.ThrowIfNull(normalizedTranscription);
        ArgumentNullException.ThrowIfNull(allowedChoices);

        var exactMatches = allowedChoices
            .Where(choice => string.Equals(
                choice,
                normalizedTranscription,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (exactMatches.Length == 1)
        {
            matchedChoice = exactMatches[0];
            return true;
        }

        if (!TryExtractSingleLabel(normalizedTranscription, out var label))
        {
            matchedChoice = string.Empty;
            return false;
        }

        var decoratedMatches = allowedChoices
            .Where(choice =>
                TryExtractSingleLabel(choice, out var allowedLabel)
                && allowedLabel == label)
            .Take(2)
            .ToArray();
        if (decoratedMatches.Length == 1)
        {
            matchedChoice = decoratedMatches[0];
            return true;
        }

        matchedChoice = string.Empty;
        return false;
    }

    private static bool TryExtractSingleLabel(string value, out Rune label)
    {
        var candidate = value.Trim();
        if (candidate.Length == 0)
        {
            label = default;
            return false;
        }

        if (candidate.Length >= 2
            && Wrappers.TryGetValue(candidate[0], out var closingWrapper)
            && candidate[^1] == closingWrapper)
        {
            candidate = candidate[1..^1].Trim();
        }
        else if (TrailingSeparators.Contains(candidate[^1]))
        {
            candidate = candidate[..^1].TrimEnd();
        }

        using var enumerator = candidate.EnumerateRunes().GetEnumerator();
        if (!enumerator.MoveNext())
        {
            label = default;
            return false;
        }

        label = enumerator.Current;
        if (enumerator.MoveNext() || !IsChoiceLabel(label))
        {
            label = default;
            return false;
        }

        return true;
    }

    private static bool IsChoiceLabel(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is
            UnicodeCategory.UppercaseLetter
            or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter
            or UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.LetterNumber
            or UnicodeCategory.OtherNumber
            or UnicodeCategory.MathSymbol
            or UnicodeCategory.ModifierSymbol
            or UnicodeCategory.OtherSymbol;
}
