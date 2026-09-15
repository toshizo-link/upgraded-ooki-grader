using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OokiGrader.SchoolManager.PassFail;

public sealed record PassFailSourceTable(
    string SourceTitle,
    IReadOnlyList<string> Columns,
    IReadOnlyList<PassFailSourceRow> Rows,
    IReadOnlyList<string> SensitiveTokens);

public sealed record PassFailSourceRow(
    string StudentNumber,
    string GradeKey,
    string ClassLabel,
    IReadOnlyList<string> Values);

public sealed record PassFailProjection(
    string Title,
    string GradeLabel,
    string ClassLabel,
    IReadOnlyList<string> Columns,
    IReadOnlyList<PassFailProjectionRow> Rows,
    string CanonicalText);

public sealed record PassFailProjectionRow(
    string Label,
    IReadOnlyList<string> Values);

public sealed class PassFailProjectionException(
    string code,
    string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static partial class PassFailGrade
{
    public static string Normalize(string? value)
    {
        var normalized = NormalizeText(value);
        foreach (var (pattern, prefix) in ExactPatterns())
        {
            var match = pattern.Match(normalized);
            if (match.Success)
            {
                return prefix + match.Groups[1].Value;
            }
        }

        var bare = BareGradePattern().Match(normalized);
        return bare.Success && int.Parse(
                bare.Groups[1].Value,
                CultureInfo.InvariantCulture) >= 4
            ? "小" + bare.Groups[1].Value
            : string.Empty;
    }

    public static string InferFromText(string? value)
    {
        var normalized = NormalizeText(value);
        var matches = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (pattern, prefix) in EmbeddedPatterns())
        {
            foreach (Match match in pattern.Matches(normalized))
            {
                matches.Add(prefix + match.Groups[1].Value);
            }
        }

        return matches.Count == 1 ? matches.Single() : string.Empty;
    }

    private static IEnumerable<(Regex Pattern, string Prefix)> ExactPatterns()
    {
        yield return (ElementaryExactPattern(), "小");
        yield return (JuniorHighExactPattern(), "中");
        yield return (HighSchoolExactPattern(), "高");
    }

    private static IEnumerable<(Regex Pattern, string Prefix)> EmbeddedPatterns()
    {
        yield return (ElementaryEmbeddedPattern(), "小");
        yield return (JuniorHighEmbeddedPattern(), "中");
        yield return (HighSchoolEmbeddedPattern(), "高");
    }

    private static string NormalizeText(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : string.Concat(value.Normalize(NormalizationForm.FormKC)
            .Where(character => !char.IsWhiteSpace(character)));

    [GeneratedRegex("^(?:小学校|小学|小)([1-6])年?$", RegexOptions.CultureInvariant)]
    private static partial Regex ElementaryExactPattern();

    [GeneratedRegex("^(?:中学校|中学|中)([1-3])年?$", RegexOptions.CultureInvariant)]
    private static partial Regex JuniorHighExactPattern();

    [GeneratedRegex("^(?:高等学校|高校|高)([1-3])年?$", RegexOptions.CultureInvariant)]
    private static partial Regex HighSchoolExactPattern();

    [GeneratedRegex("^([1-6])年?$", RegexOptions.CultureInvariant)]
    private static partial Regex BareGradePattern();

    [GeneratedRegex("(?:小学校|小学|小)([1-6])年?", RegexOptions.CultureInvariant)]
    private static partial Regex ElementaryEmbeddedPattern();

    [GeneratedRegex("(?:中学校|中学|中)([1-3])年?", RegexOptions.CultureInvariant)]
    private static partial Regex JuniorHighEmbeddedPattern();

    [GeneratedRegex("(?:高等学校|高校|高)([1-3])年?", RegexOptions.CultureInvariant)]
    private static partial Regex HighSchoolEmbeddedPattern();
}

public static class PassFailTableProjector
{
    public static PassFailProjection Project(
        PassFailSourceTable source,
        string targetStudentNumber,
        string targetGradeLabel,
        string targetClassLabel)
    {
        ArgumentNullException.ThrowIfNull(source);
        var normalizedNumber = NormalizeIdentity(targetStudentNumber);
        var gradeKey = PassFailGrade.Normalize(targetGradeLabel);
        var normalizedClass = NormalizeIdentity(targetClassLabel);
        if (normalizedNumber.Length == 0
            || gradeKey.Length == 0
            || normalizedClass.Length == 0)
        {
            throw new PassFailProjectionException(
                "target_identity_missing",
                "The target student number, grade, and class are required.");
        }

        if (source.Columns.Count == 0)
        {
            throw new PassFailProjectionException(
                "pass_fail_columns_missing",
                "The pass/fail table has no distributable columns.");
        }

        var classRows = source.Rows
            .Where(row => row.GradeKey == gradeKey
                && NormalizeIdentity(row.ClassLabel) == normalizedClass)
            .ToArray();
        var targets = classRows
            .Where(row => NormalizeIdentity(row.StudentNumber) == normalizedNumber)
            .ToArray();
        if (targets.Length != 1)
        {
            throw new PassFailProjectionException(
                targets.Length == 0 ? "target_not_found" : "target_ambiguous",
                "The pass/fail table must contain exactly one matching target row.");
        }

        var peerSequence = 0;
        var rows = classRows.Select(row =>
        {
            if (row.Values.Count != source.Columns.Count)
            {
                throw new PassFailProjectionException(
                    "pass_fail_row_width_invalid",
                    "A pass/fail row does not match the header width.");
            }

            var isTarget = ReferenceEquals(row, targets[0]);
            var label = isTarget
                ? "本人"
                : $"同級生{++peerSequence:00}";
            return new PassFailProjectionRow(label, row.Values.ToArray());
        }).ToArray();
        ValidatePrivacy(source, rows);

        var title = $"合否表（{gradeKey}）";
        var canonical = new StringBuilder()
            .Append(title).Append('\n')
            .Append(targetClassLabel.Trim()).Append('\n')
            .Append("識別").Append('\t')
            .AppendJoin('\t', source.Columns.Select(column => column.Trim()))
            .Append('\n');
        foreach (var row in rows)
        {
            canonical.Append(row.Label).Append('\t')
                .AppendJoin('\t', row.Values.Select(value => value.Trim()))
                .Append('\n');
        }

        return new PassFailProjection(
            title,
            gradeKey,
            targetClassLabel.Trim(),
            source.Columns.Select(column => column.Trim()).ToArray(),
            rows,
            canonical.ToString());
    }

    public static string NormalizeIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Concat(
            value.Normalize(NormalizationForm.FormKC)
                .Where(character => !char.IsWhiteSpace(character)))
            .ToUpper(CultureInfo.InvariantCulture);
    }

    private static void ValidatePrivacy(
        PassFailSourceTable source,
        IReadOnlyList<PassFailProjectionRow> rows)
    {
        var sensitive = source.SensitiveTokens
            .Select(NormalizeIdentity)
            .Where(token => token.Length >= 2)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var values = source.Columns.Concat(rows.SelectMany(row => row.Values));
        foreach (var value in values)
        {
            var normalized = NormalizeIdentity(value);
            if (ContainsLongDigitSequence(normalized)
                || sensitive.Any(token => normalized.Contains(
                    token,
                    StringComparison.Ordinal)))
            {
                throw new PassFailProjectionException(
                    "pass_fail_privacy_validation_failed",
                    "The pass/fail output still contains an identity token.");
            }
        }
    }

    private static bool ContainsLongDigitSequence(string value)
    {
        var length = 0;
        foreach (var character in value)
        {
            length = char.IsDigit(character) ? length + 1 : 0;
            if (length >= 5)
            {
                return true;
            }
        }

        return false;
    }
}
