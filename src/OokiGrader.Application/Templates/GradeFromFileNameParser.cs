using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OokiGrader.Domain.Templates;

namespace OokiGrader.Application.Templates;

public sealed record FileNameGradeResult(
    GradeLevel Grade,
    bool IsUnambiguous,
    string? MatchedToken,
    string? ErrorCode);

/// <summary>
/// Extracts only explicit grade labels from an uploaded file name. Bare
/// numbers, dates, page numbers, test iterations, and STEP suffixes are not
/// grade evidence.
/// </summary>
public static class GradeFromFileNameParser
{
    public const string ConflictErrorCode = "FILENAME_GRADE_CONFLICT";

    private static readonly Regex CollapsibleWhitespace = new(
        @"\s+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex[] ExplicitGradePatterns =
    [
        new(
            @"小学\s*(?<grade>[1-6])\s*年(?:\s*生)?",
            RegexOptions.CultureInvariant | RegexOptions.Compiled),
        new(
            @"小\s*(?<grade>[1-6])(?![0-9])",
            RegexOptions.CultureInvariant | RegexOptions.Compiled),
        new(
            @"(?<![A-Z0-9])(?<!令和)(?<!平成)(?<!昭和)(?<!大正)(?<!明治)(?<grade>[1-6])\s*年(?:\s*生)?(?!\s*(?:0?[1-9]|1[0-2])\s*月)",
            RegexOptions.IgnoreCase
                | RegexOptions.CultureInvariant
                | RegexOptions.Compiled),
        new(
            @"(?<![A-Z0-9])G\s*(?<grade>[1-6])(?![A-Z0-9])",
            RegexOptions.IgnoreCase
                | RegexOptions.CultureInvariant
                | RegexOptions.Compiled),
        new(
            @"(?<![A-Z0-9])GRADE\s*(?<grade>[1-6])(?![A-Z0-9])",
            RegexOptions.IgnoreCase
                | RegexOptions.CultureInvariant
                | RegexOptions.Compiled),
    ];

    public static FileNameGradeResult Parse(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var baseName = Path.GetFileNameWithoutExtension(fileName)
            .Normalize(NormalizationForm.FormKC);
        var normalized = CollapsibleWhitespace.Replace(baseName, " ").Trim();
        var matches = ExplicitGradePatterns
            .SelectMany(pattern => pattern.Matches(normalized).Cast<Match>())
            .Select(match => new GradeMatch(
                Grade: (GradeLevel)int.Parse(
                    match.Groups["grade"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture),
                Token: match.Value,
                Index: match.Index))
            .OrderBy(match => match.Index)
            .ThenByDescending(match => match.Token.Length)
            .ToArray();
        var grades = matches
            .Select(match => match.Grade)
            .Distinct()
            .ToArray();

        return grades.Length switch
        {
            0 => new FileNameGradeResult(
                GradeLevel.Unknown,
                IsUnambiguous: false,
                MatchedToken: null,
                ErrorCode: null),
            1 => new FileNameGradeResult(
                grades[0],
                IsUnambiguous: true,
                MatchedToken: matches[0].Token,
                ErrorCode: null),
            _ => new FileNameGradeResult(
                GradeLevel.Unknown,
                IsUnambiguous: false,
                MatchedToken: null,
                ErrorCode: ConflictErrorCode),
        };
    }

    private sealed record GradeMatch(
        GradeLevel Grade,
        string Token,
        int Index);
}
