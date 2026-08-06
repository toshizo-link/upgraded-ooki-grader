using OokiGrader.Domain.Students;

namespace OokiGrader.Domain.Tests;

public sealed class NameMatchingTests
{
    [Fact]
    public void NameNormalizationRemovesWidthAndJapaneseSpaces()
    {
        Assert.Equal("山田太郎", StudentNameNormalizer.NormalizeName("　山田　太郎 "));
        Assert.Equal("AB12", StudentNameNormalizer.NormalizeStudentNumber("ａｂ １２"));
    }

    [Fact]
    public void NameNormalizationPreservesKanaAndKanjiDistinctions()
    {
        Assert.NotEqual(
            StudentNameNormalizer.NormalizeName("渡辺"),
            StudentNameNormalizer.NormalizeName("わたなべ"));
        Assert.NotEqual(
            StudentNameNormalizer.NormalizeName("わたなべ"),
            StudentNameNormalizer.NormalizeName("ワタナベ"));
    }

    [Fact]
    public void StoredOldSurnameAliasCanMatchExactly()
    {
        var candidate = new StudentNameCandidate(
            "student-1",
            "佐藤花子",
            aliases: ["鈴木花子"]);

        var score = StudentNameMatcher.ScoreCandidate(
            new NameObservation("鈴木 花子", null),
            candidate);

        Assert.True(score.ExactAlias);
        Assert.True(score.Score >= 0.98);
    }

    [Fact]
    public void StudentNumberConflictOverridesExactNameSimilarity()
    {
        var candidate = new StudentNameCandidate(
            "student-1",
            "山田太郎",
            studentNumber: "100");

        var score = StudentNameMatcher.ScoreCandidate(
            new NameObservation("山田太郎", "999"),
            candidate);

        Assert.True(score.StudentNumberConflict);
        Assert.Equal(0, score.Score);
    }

    [Fact]
    public void CalibrationGateDisablesAutomaticAssignment()
    {
        var result = StudentNameMatcher.Match(
            new NameObservation("山田太郎", "100"),
            [new StudentNameCandidate("student-1", "山田太郎", "100")],
            new NameMatchPolicy(calibrationApproved: false));

        Assert.Equal(NameMatchDisposition.NeedsReview, result.Disposition);
        Assert.Null(result.AssignedStudentId);
    }

    [Fact]
    public void ExactCompatibleNumberCanAutoAssignAfterCalibration()
    {
        var result = StudentNameMatcher.Match(
            new NameObservation("山田 太郎", "１００"),
            [
                new StudentNameCandidate("student-1", "山田太郎", "100"),
                new StudentNameCandidate("student-2", "山田次郎", "200"),
            ],
            new NameMatchPolicy(calibrationApproved: true));

        Assert.Equal(NameMatchDisposition.AutoAssigned, result.Disposition);
        Assert.Equal("student-1", result.AssignedStudentId);
    }

    [Fact]
    public void CloseNamesWithoutEnoughMarginRequireReview()
    {
        var result = StudentNameMatcher.Match(
            new NameObservation("山田太"),
            [
                new StudentNameCandidate("student-1", "山田太郎"),
                new StudentNameCandidate("student-2", "山田太一"),
            ],
            new NameMatchPolicy(
                calibrationApproved: true,
                autoAssignmentThreshold: 0.80,
                minimumFirstSecondMargin: 0.10));

        Assert.Equal(NameMatchDisposition.NeedsReview, result.Disposition);
    }

    [Fact]
    public void InactiveCandidateNeverAutoAssigns()
    {
        var result = StudentNameMatcher.Match(
            new NameObservation("山田太郎", "100"),
            [
                new StudentNameCandidate(
                    "student-1",
                    "山田太郎",
                    "100",
                    isActive: false),
            ],
            new NameMatchPolicy(
                calibrationApproved: true,
                autoAssignmentThreshold: 0.80));

        Assert.Equal(NameMatchDisposition.NeedsReview, result.Disposition);
    }

    [Fact]
    public void DuplicateSubmissionConflictNeverAutoAssigns()
    {
        var result = StudentNameMatcher.Match(
            new NameObservation(
                "山田太郎",
                "100",
                DuplicateSubmissionConflict: true),
            [new StudentNameCandidate("student-1", "山田太郎", "100")],
            new NameMatchPolicy(calibrationApproved: true));

        Assert.Equal(NameMatchDisposition.NeedsReview, result.Disposition);
    }

    [Fact]
    public void ReviewResultReturnsAtMostFiveCandidates()
    {
        var candidates = Enumerable.Range(1, 10)
            .Select(index => new StudentNameCandidate(
                $"student-{index}",
                $"山田太{index}"))
            .ToArray();

        var result = StudentNameMatcher.Match(
            new NameObservation("山田太郎"),
            candidates,
            new NameMatchPolicy(calibrationApproved: false));

        Assert.Equal(5, result.Candidates.Count);
    }
}
