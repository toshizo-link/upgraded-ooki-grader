using System.Globalization;
using System.Text;

namespace OokiGrader.Application.Identity;

public sealed record IdentityTranscription(
    string? VisibleName,
    string? VisibleStudentNumber,
    string Legibility,
    int ProviderConfidenceBasisPoints);

public sealed record RosterIdentityAlias(
    string Value,
    bool RecognitionEnabled = true);

public sealed record RosterIdentityCandidate(
    string StudentId,
    string StudentNumber,
    string FamilyName,
    string GivenName,
    string DisplayName,
    string? FamilyNameKana,
    string? GivenNameKana,
    bool Expected,
    IReadOnlyList<RosterIdentityAlias> Aliases);

public sealed record IdentityCandidateMatch(
    string StudentId,
    int RankScore,
    bool Expected,
    bool ExactStudentNumber,
    bool ExactFullName,
    bool ExactAlias,
    bool ExactStoredKana,
    bool StudentNumberConflict,
    int NameSimilarityBasisPoints,
    IReadOnlyList<string> Evidence);

public sealed record IdentityMatchResult(
    string PolicyVersion,
    string Disposition,
    string? NormalizedVisibleName,
    string? NormalizedVisibleStudentNumber,
    IReadOnlyList<IdentityCandidateMatch> Candidates,
    int? FirstSecondMargin,
    bool AutomaticAssignmentEnabled);

/// <summary>
/// Produces deterministic, interpretable roster candidates from a visual
/// transcription. It deliberately does not auto-assign: the launch precision
/// gate requires a separate, school-calibrated policy revision.
/// </summary>
public static class LocalRosterMatcher
{
    public const string PolicyVersion = "local-roster-review-v1";

    private const int MaximumCandidates = 5;

    public static IdentityMatchResult Match(
        IdentityTranscription transcription,
        IReadOnlyCollection<RosterIdentityCandidate> roster)
    {
        ArgumentNullException.ThrowIfNull(transcription);
        ArgumentNullException.ThrowIfNull(roster);
        if (transcription.ProviderConfidenceBasisPoints is < 0 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(transcription),
                "Provider confidence must be between 0 and 10,000.");
        }

        var visibleName = NormalizeName(transcription.VisibleName);
        var visibleNumber = NormalizeStudentNumber(
            transcription.VisibleStudentNumber);
        if (transcription.Legibility is "blank" or "unreadable"
            || (visibleName is null && visibleNumber is null))
        {
            return new IdentityMatchResult(
                PolicyVersion,
                "no_match",
                visibleName,
                visibleNumber,
                [],
                null,
                AutomaticAssignmentEnabled: false);
        }

        var matches = roster
            .Select(candidate => Score(candidate, visibleName, visibleNumber))
            .Where(match => match.RankScore > 0)
            .OrderByDescending(match => match.RankScore)
            .ThenByDescending(match => match.ExactStudentNumber)
            .ThenByDescending(match => match.Expected)
            .ThenBy(match => match.StudentId, StringComparer.Ordinal)
            .Take(MaximumCandidates)
            .ToArray();
        int? margin = matches.Length switch
        {
            0 => null,
            1 => matches[0].RankScore,
            _ => matches[0].RankScore - matches[1].RankScore,
        };

        return new IdentityMatchResult(
            PolicyVersion,
            matches.Length == 0 ? "no_match" : "needs_review",
            visibleName,
            visibleNumber,
            matches,
            margin,
            AutomaticAssignmentEnabled: false);
    }

    public static string? NormalizeName(string? value)
    {
        var normalized = NormalizeWidth(value);
        if (normalized is null)
        {
            return null;
        }

        var builder = new StringBuilder(normalized.Length);
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (!Rune.IsWhiteSpace(rune))
            {
                builder.Append(rune);
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    public static string? NormalizeStudentNumber(string? value)
    {
        var normalized = NormalizeWidth(value);
        if (normalized is null)
        {
            return null;
        }

        var builder = new StringBuilder(normalized.Length);
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                continue;
            }

            var scalar = rune.Value;
            if (scalar is 0x2010 or 0x2011 or 0x2012 or 0x2013
                or 0x2014 or 0x2212 or 0x30FC)
            {
                builder.Append('-');
            }
            else
            {
                builder.Append(rune);
            }
        }

        return builder.Length == 0
            ? null
            : builder.ToString().ToUpperInvariant();
    }

    private static IdentityCandidateMatch Score(
        RosterIdentityCandidate candidate,
        string? visibleName,
        string? visibleNumber)
    {
        if (string.IsNullOrWhiteSpace(candidate.StudentId))
        {
            throw new ArgumentException(
                "Every roster candidate requires a stable student ID.",
                nameof(candidate));
        }

        var candidateNumber = NormalizeStudentNumber(candidate.StudentNumber);
        var exactNumber = visibleNumber is not null
            && candidateNumber is not null
            && string.Equals(
                visibleNumber,
                candidateNumber,
                StringComparison.Ordinal);
        var numberConflict = visibleNumber is not null
            && candidateNumber is not null
            && !exactNumber;

        var canonicalNames = new[]
            {
                NormalizeName(candidate.FamilyName + candidate.GivenName),
                NormalizeName(candidate.GivenName + candidate.FamilyName),
                NormalizeName(candidate.DisplayName),
            }
            .Where(value => value is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var aliases = candidate.Aliases
            .Where(alias => alias.RecognitionEnabled)
            .Select(alias => NormalizeName(alias.Value))
            .Where(value => value is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var storedKana = NormalizeName(
            (candidate.FamilyNameKana ?? string.Empty)
            + (candidate.GivenNameKana ?? string.Empty));

        var exactName = visibleName is not null
            && canonicalNames.Contains(visibleName, StringComparer.Ordinal);
        var exactAlias = visibleName is not null
            && aliases.Contains(visibleName, StringComparer.Ordinal);
        var exactKana = visibleName is not null
            && storedKana is not null
            && string.Equals(visibleName, storedKana, StringComparison.Ordinal);
        var comparisonNames = canonicalNames
            .Concat(aliases)
            .Append(storedKana)
            .Where(value => value is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var similarity = visibleName is null
            ? 0
            : comparisonNames
                .Select(value => SimilarityBasisPoints(visibleName, value))
                .DefaultIfEmpty(0)
                .Max();

        var score = exactNumber
            ? 9_000
            : exactName
                ? 8_500
                : exactAlias
                    ? 8_200
                    : exactKana
                        ? 8_000
                        : (int)Math.Round(
                            similarity * 0.7,
                            MidpointRounding.AwayFromZero);
        if (candidate.Expected)
        {
            score = checked(score + 250);
        }

        if (numberConflict)
        {
            score = Math.Max(0, score - 3_000);
        }

        var evidence = new List<string>(5);
        AddEvidence(evidence, exactNumber, "exact_student_number");
        AddEvidence(evidence, exactName, "exact_full_name");
        AddEvidence(evidence, exactAlias, "exact_alias");
        AddEvidence(evidence, exactKana, "exact_stored_kana");
        AddEvidence(evidence, candidate.Expected, "expected_roster");
        AddEvidence(evidence, numberConflict, "student_number_conflict");
        if (similarity > 0 && !exactName && !exactAlias && !exactKana)
        {
            evidence.Add("normalized_name_similarity");
        }

        return new IdentityCandidateMatch(
            candidate.StudentId,
            Math.Min(10_000, score),
            candidate.Expected,
            exactNumber,
            exactName,
            exactAlias,
            exactKana,
            numberConflict,
            similarity,
            evidence);
    }

    private static void AddEvidence(
        List<string> evidence,
        bool condition,
        string value)
    {
        if (condition)
        {
            evidence.Add(value);
        }
    }

    private static int SimilarityBasisPoints(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return 10_000;
        }

        var leftRunes = left.EnumerateRunes().ToArray();
        var rightRunes = right.EnumerateRunes().ToArray();
        var maximum = Math.Max(leftRunes.Length, rightRunes.Length);
        if (maximum == 0)
        {
            return 10_000;
        }

        var previous = new int[rightRunes.Length + 1];
        var current = new int[rightRunes.Length + 1];
        for (var index = 0; index <= rightRunes.Length; index++)
        {
            previous[index] = index;
        }

        for (var leftIndex = 1; leftIndex <= leftRunes.Length; leftIndex++)
        {
            current[0] = leftIndex;
            for (var rightIndex = 1; rightIndex <= rightRunes.Length; rightIndex++)
            {
                var substitution = leftRunes[leftIndex - 1]
                    == rightRunes[rightIndex - 1]
                    ? 0
                    : 1;
                current[rightIndex] = Math.Min(
                    Math.Min(
                        checked(previous[rightIndex] + 1),
                        checked(current[rightIndex - 1] + 1)),
                    checked(previous[rightIndex - 1] + substitution));
            }

            (previous, current) = (current, previous);
        }

        var distance = previous[rightRunes.Length];
        return Math.Clamp(
            (int)Math.Round(
                (1d - ((double)distance / maximum)) * 10_000,
                MidpointRounding.AwayFromZero),
            0,
            10_000);
    }

    private static string? NormalizeWidth(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().Normalize(NormalizationForm.FormKC);
    }
}
