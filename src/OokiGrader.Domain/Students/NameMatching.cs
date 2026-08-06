using System.Collections.ObjectModel;
using System.Text;
using OokiGrader.Domain.Common;
using OokiGrader.Domain.Grading;

namespace OokiGrader.Domain.Students;

public enum NameEvidenceQuality
{
    Clear,
    Blank,
    Unreadable,
    Cropped,
}

public enum NameMatchDisposition
{
    NoMatch,
    NeedsReview,
    AutoAssigned,
}

public static class StudentNameNormalizer
{
    private static readonly JapaneseNormalizationOptions NameOptions = new()
    {
        CollapseWhitespace = false,
        RemoveAllWhitespace = true,
        TrimOuterWhitespace = true,
    };

    public static string NormalizeName(string? value) =>
        JapaneseTextNormalizer.NormalizeForComparison(value, NameOptions);

    public static string NormalizeStudentNumber(string? value)
    {
        var normalized = JapaneseTextNormalizer.NormalizeForComparison(value, NameOptions);
        return normalized.ToUpperInvariant();
    }

    public static double Similarity(string? left, string? right)
    {
        var leftRunes = NormalizeName(left).EnumerateRunes().ToArray();
        var rightRunes = NormalizeName(right).EnumerateRunes().ToArray();
        var longest = Math.Max(leftRunes.Length, rightRunes.Length);
        if (longest == 0)
        {
            return 1;
        }

        var previous = new int[rightRunes.Length + 1];
        var current = new int[rightRunes.Length + 1];
        for (var column = 0; column <= rightRunes.Length; column++)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= leftRunes.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= rightRunes.Length; column++)
            {
                var substitutionCost = leftRunes[row - 1] == rightRunes[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(
                        current[column - 1] + 1,
                        previous[column] + 1),
                    previous[column - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return 1d - ((double)previous[rightRunes.Length] / longest);
    }
}

public sealed class StudentNameCandidate
{
    private readonly ReadOnlyCollection<string> _aliases;

    public StudentNameCandidate(
        string studentId,
        string displayName,
        string? studentNumber = null,
        IEnumerable<string>? aliases = null,
        bool isActive = true,
        bool isExpected = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(studentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        StudentId = studentId;
        DisplayName = displayName;
        NormalizedName = StudentNameNormalizer.NormalizeName(displayName);
        StudentNumber = string.IsNullOrWhiteSpace(studentNumber) ? null : studentNumber;
        NormalizedStudentNumber = StudentNameNormalizer.NormalizeStudentNumber(studentNumber);
        _aliases = Array.AsReadOnly(
            (aliases ?? [])
                .Select(StudentNameNormalizer.NormalizeName)
                .Where(alias => alias.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray());
        IsActive = isActive;
        IsExpected = isExpected;
    }

    public string StudentId { get; }

    public string DisplayName { get; }

    public string NormalizedName { get; }

    public string? StudentNumber { get; }

    public string NormalizedStudentNumber { get; }

    public IReadOnlyList<string> NormalizedAliases => _aliases;

    public bool IsActive { get; }

    public bool IsExpected { get; }
}

public sealed record NameObservation(
    string? VisibleName,
    string? StudentNumber = null,
    NameEvidenceQuality Quality = NameEvidenceQuality.Clear,
    bool DuplicateSubmissionConflict = false);

public sealed record NameCandidateScore(
    StudentNameCandidate Candidate,
    double Score,
    bool ExactStudentNumber,
    bool ExactName,
    bool ExactAlias,
    double NameSimilarity,
    bool StudentNumberConflict,
    bool NameConflict);

public sealed class NameMatchPolicy
{
    public NameMatchPolicy(
        bool calibrationApproved,
        double autoAssignmentThreshold = 0.98,
        double minimumFirstSecondMargin = 0.05,
        int maximumCandidates = 5)
    {
        if (autoAssignmentThreshold is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(autoAssignmentThreshold));
        }

        if (minimumFirstSecondMargin is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumFirstSecondMargin));
        }

        if (maximumCandidates is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCandidates));
        }

        CalibrationApproved = calibrationApproved;
        AutoAssignmentThreshold = autoAssignmentThreshold;
        MinimumFirstSecondMargin = minimumFirstSecondMargin;
        MaximumCandidates = maximumCandidates;
    }

    public bool CalibrationApproved { get; }

    public double AutoAssignmentThreshold { get; }

    public double MinimumFirstSecondMargin { get; }

    public int MaximumCandidates { get; }
}

public sealed class NameMatchResult
{
    private readonly ReadOnlyCollection<NameCandidateScore> _candidates;

    internal NameMatchResult(
        NameMatchDisposition disposition,
        string? assignedStudentId,
        IEnumerable<NameCandidateScore> candidates,
        string reason)
    {
        Disposition = disposition;
        AssignedStudentId = assignedStudentId;
        _candidates = Array.AsReadOnly(candidates.ToArray());
        Reason = reason;
    }

    public NameMatchDisposition Disposition { get; }

    public string? AssignedStudentId { get; }

    public IReadOnlyList<NameCandidateScore> Candidates => _candidates;

    public string Reason { get; }
}

public static class StudentNameMatcher
{
    public static NameCandidateScore ScoreCandidate(
        NameObservation observation,
        StudentNameCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(candidate);

        var observedName = StudentNameNormalizer.NormalizeName(observation.VisibleName);
        var observedNumber = StudentNameNormalizer.NormalizeStudentNumber(
            observation.StudentNumber);
        var exactName = observedName.Length > 0
            && string.Equals(observedName, candidate.NormalizedName, StringComparison.Ordinal);
        var exactAlias = observedName.Length > 0
            && candidate.NormalizedAliases.Contains(observedName, StringComparer.Ordinal);
        var similarity = observedName.Length == 0
            ? 0
            : Math.Max(
                StudentNameNormalizer.Similarity(observedName, candidate.NormalizedName),
                candidate.NormalizedAliases
                    .Select(alias => StudentNameNormalizer.Similarity(observedName, alias))
                    .DefaultIfEmpty(0)
                    .Max());
        var bothNumbersPresent = observedNumber.Length > 0
            && candidate.NormalizedStudentNumber.Length > 0;
        var candidateNumberUnavailable = observedNumber.Length > 0
            && candidate.NormalizedStudentNumber.Length == 0;
        var exactNumber = bothNumbersPresent
            && string.Equals(
                observedNumber,
                candidate.NormalizedStudentNumber,
                StringComparison.Ordinal);
        var numberConflict = bothNumbersPresent && !exactNumber;
        var nameConflict = exactNumber && observedName.Length > 0 && similarity < 0.5;

        double score;
        if (numberConflict)
        {
            score = 0;
        }
        else if (exactNumber && !nameConflict)
        {
            score = exactName || exactAlias ? 1 : 0.99;
        }
        else if (exactName)
        {
            score = 0.985;
        }
        else if (exactAlias)
        {
            score = 0.98;
        }
        else
        {
            score = 0.45 + (0.5 * similarity);
        }

        if (candidate.IsExpected && !numberConflict && !nameConflict)
        {
            score = Math.Min(1, score + 0.005);
        }

        if (!candidate.IsActive || nameConflict)
        {
            score = Math.Min(score, 0.89);
        }
        else if (candidateNumberUnavailable)
        {
            score = Math.Min(score, 0.95);
        }

        return new NameCandidateScore(
            candidate,
            score,
            exactNumber,
            exactName,
            exactAlias,
            similarity,
            numberConflict,
            nameConflict);
    }

    public static NameMatchResult Match(
        NameObservation observation,
        IEnumerable<StudentNameCandidate> candidates,
        NameMatchPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(policy);

        if (observation.Quality != NameEvidenceQuality.Clear
            || (string.IsNullOrWhiteSpace(observation.VisibleName)
                && string.IsNullOrWhiteSpace(observation.StudentNumber)))
        {
            return new NameMatchResult(
                NameMatchDisposition.NoMatch,
                null,
                [],
                "The name artifact is blank or unreadable.");
        }

        var ranked = candidates
            .Select(candidate => ScoreCandidate(observation, candidate))
            .OrderByDescending(score => score.Score)
            .ThenByDescending(score => score.Candidate.IsExpected)
            .ThenBy(score => score.Candidate.StudentId, StringComparer.Ordinal)
            .ToArray();

        if (ranked.Length == 0)
        {
            return new NameMatchResult(
                NameMatchDisposition.NoMatch,
                null,
                [],
                "No roster candidates are available.");
        }

        var visible = ranked.Take(policy.MaximumCandidates).ToArray();
        var top = ranked[0];
        var margin = ranked.Length == 1 ? 1 : top.Score - ranked[1].Score;
        var duplicateNumber = top.ExactStudentNumber
            && ranked.Count(score => score.ExactStudentNumber) > 1;
        var conflict = observation.DuplicateSubmissionConflict
            || top.StudentNumberConflict
            || top.NameConflict
            || duplicateNumber
            || !top.Candidate.IsActive;

        if (policy.CalibrationApproved
            && !conflict
            && top.Score >= policy.AutoAssignmentThreshold
            && margin >= policy.MinimumFirstSecondMargin)
        {
            return new NameMatchResult(
                NameMatchDisposition.AutoAssigned,
                top.Candidate.StudentId,
                visible,
                "The calibrated score and first/second margin passed policy.");
        }

        return new NameMatchResult(
            NameMatchDisposition.NeedsReview,
            null,
            visible,
            !policy.CalibrationApproved
                ? "Automatic assignment is disabled until calibration is approved."
                : "Confidence, margin, activity, or conflict policy requires review.");
    }
}
