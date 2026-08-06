using System.Globalization;
using System.Text;

namespace OokiGrader.Domain.Grading;

public sealed record JapaneseNormalizationOptions
{
    public bool TrimOuterWhitespace { get; init; } = true;

    public bool CollapseWhitespace { get; init; } = true;

    public bool RemoveAllWhitespace { get; init; }
}

public static class JapaneseTextNormalizer
{
    public static string NormalizeForExactMatch(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        return input.Normalize(NormalizationForm.FormC).Trim();
    }

    public static string NormalizeForComparison(
        string? input,
        JapaneseNormalizationOptions? options = null)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        options ??= new JapaneseNormalizationOptions();
        var normalized = input.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        var pendingWhitespace = false;

        foreach (var rune in normalized.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                if (options.RemoveAllWhitespace)
                {
                    continue;
                }

                if (options.CollapseWhitespace)
                {
                    pendingWhitespace = builder.Length > 0;
                    continue;
                }
            }

            if (pendingWhitespace)
            {
                builder.Append(' ');
                pendingWhitespace = false;
            }

            builder.Append(rune.ToString());
        }

        var result = builder.ToString();
        return options.TrimOuterWhitespace ? result.Trim() : result;
    }

    public static bool ComparisonEquals(string? left, string? right) =>
        string.Equals(
            NormalizeForComparison(left),
            NormalizeForComparison(right),
            StringComparison.Ordinal);

    public static bool ExactEquals(string? left, string? right) =>
        string.Equals(
            NormalizeForExactMatch(left),
            NormalizeForExactMatch(right),
            StringComparison.Ordinal);
}

public static class KanjiDetector
{
    public static bool ContainsKanji(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        return value.EnumerateRunes().Any(IsKanji);
    }

    public static bool IsKanji(Rune rune)
    {
        var value = rune.Value;

        return value is 0x3005 or 0x3006 or 0x3007
            || value is >= 0x3400 and <= 0x4DBF
            || value is >= 0x4E00 and <= 0x9FFF
            || value is >= 0xF900 and <= 0xFAFF
            || value is >= 0x20000 and <= 0x2A6DF
            || value is >= 0x2A700 and <= 0x2B73F
            || value is >= 0x2B740 and <= 0x2B81F
            || value is >= 0x2B820 and <= 0x2CEAF
            || value is >= 0x2CEB0 and <= 0x2EBEF
            || value is >= 0x30000 and <= 0x3134F;
    }
}
