using System.Text;
using OokiGrader.Application.Identity;

namespace OokiGrader.Application.Tests;

public sealed class LocalRosterMatcherTests
{
    [Fact]
    public void NormalizesWidthAndSpacingWithoutTransliteratingJapaneseScript()
    {
        Assert.Equal("大木花子", LocalRosterMatcher.NormalizeName(" 大木　花子 "));
        Assert.Equal("オオキ", LocalRosterMatcher.NormalizeName("ｵｵｷ"));
        Assert.NotEqual(
            LocalRosterMatcher.NormalizeName("大木"),
            LocalRosterMatcher.NormalizeName("おおき"));
        Assert.Equal(
            "S-１０４２".Normalize(NormalizationForm.FormKC),
            LocalRosterMatcher.NormalizeStudentNumber(" s－１０４２ ")
                ?.ToUpperInvariant());
    }

    [Fact]
    public void RanksExactNumberButFlagsAnIncompatibleVisibleName()
    {
        var result = LocalRosterMatcher.Match(
            new IdentityTranscription("鈴木太郎", "S-1042", "clear", 9_500),
            [
                Student(
                    "student-a",
                    "S-1042",
                    "大木",
                    "花子",
                    expected: true),
                Student(
                    "student-b",
                    "S-9999",
                    "鈴木",
                    "太郎",
                    expected: true),
            ]);

        Assert.Equal("needs_review", result.Disposition);
        Assert.False(result.AutomaticAssignmentEnabled);
        Assert.Equal("student-a", result.Candidates[0].StudentId);
        Assert.True(result.Candidates[0].ExactStudentNumber);
        Assert.Contains(
            result.Candidates[0].Evidence,
            value => value == "exact_student_number");
        Assert.True(result.Candidates[1].StudentNumberConflict);
    }

    [Fact]
    public void UsesOnlyStoredKanaAndRecognitionEnabledAliases()
    {
        var result = LocalRosterMatcher.Match(
            new IdentityTranscription("オオキハナコ", null, "clear", 9_000),
            [
                Student(
                    "student-a",
                    "S-1",
                    "大木",
                    "花子",
                    familyKana: "オオキ",
                    givenKana: "ハナコ",
                    aliases: [new("おおきはなこ", RecognitionEnabled: false)]),
                Student(
                    "student-b",
                    "S-2",
                    "大樹",
                    "花子",
                    aliases: [new("オオキハナコ")]),
            ]);

        Assert.True(result.Candidates[0].ExactAlias);
        Assert.Equal("student-b", result.Candidates[0].StudentId);
        Assert.True(result.Candidates[1].ExactStoredKana);
        Assert.Equal("student-a", result.Candidates[1].StudentId);
    }

    [Fact]
    public void ExpectedRosterBreaksAnOtherwiseEquivalentTie()
    {
        var result = LocalRosterMatcher.Match(
            new IdentityTranscription("山田太郎", null, "ambiguous", 6_000),
            [
                Student("student-b", "2", "山田", "太郎", expected: false),
                Student("student-a", "1", "山田", "太郎", expected: true),
            ]);

        Assert.Equal("student-a", result.Candidates[0].StudentId);
        Assert.Equal(250, result.FirstSecondMargin);
    }

    [Theory]
    [InlineData("blank")]
    [InlineData("unreadable")]
    public void BlankOrUnreadableEvidenceProducesNoCandidate(string legibility)
    {
        var result = LocalRosterMatcher.Match(
            new IdentityTranscription("大木花子", "S-1", legibility, 0),
            [Student("student-a", "S-1", "大木", "花子")]);

        Assert.Equal("no_match", result.Disposition);
        Assert.Empty(result.Candidates);
    }

    private static RosterIdentityCandidate Student(
        string id,
        string number,
        string family,
        string given,
        bool expected = false,
        string? familyKana = null,
        string? givenKana = null,
        IReadOnlyList<RosterIdentityAlias>? aliases = null) =>
        new(
            id,
            number,
            family,
            given,
            family + " " + given,
            familyKana,
            givenKana,
            expected,
            aliases ?? []);
}
