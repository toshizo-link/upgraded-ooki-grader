using OokiGrader.SchoolManager.Delivery;

namespace OokiGrader.SchoolManager.Tests;

public sealed class RecipientMatcherTests
{
    [Fact]
    public void MatchRequiresOneExactStudent()
    {
        var candidates = new[]
        {
            new SchoolManagerStudentCandidate(
                "student-1",
                "department-1",
                "１２３４５６７８",
                "山田 太郎"),
            new SchoolManagerStudentCandidate(
                "student-2",
                "department-2",
                "12345678-2",
                "山田太郎"),
        };

        var match = SchoolManagerRecipientMatcher.Match(
            "12345678",
            "山田　太郎",
            candidates);

        Assert.Equal("student-1", match.UserId);
    }

    [Fact]
    public void MatchFailsClosedForAmbiguityAndMismatch()
    {
        Assert.Throws<SchoolManagerRecipientException>(() =>
            SchoolManagerRecipientMatcher.Match(
                "12345678",
                "山田太郎",
                [
                    new("1", "d1", "12345678", "山田太郎"),
                    new("2", "d2", "12345678", "山田太郎"),
                ]));
        Assert.Throws<SchoolManagerRecipientException>(() =>
            SchoolManagerRecipientMatcher.Match(
                "12345678",
                "山田太郎",
                [new("1", "d1", "12345678", "山田花子")]));
    }
}
