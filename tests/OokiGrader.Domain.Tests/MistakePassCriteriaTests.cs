using OokiGrader.Domain.Grading;

namespace OokiGrader.Domain.Tests;

public sealed class MistakePassCriteriaTests
{
    [Theory]
    [InlineData(null, "unconfigured")]
    [InlineData(0, "fail")]
    [InlineData(3, "fail")]
    [InlineData(4, "pass")]
    [InlineData(5, "pass")]
    public void CountsNonCorrectQuestionsAndIncludesBoundary(int? maximum, string expected)
    {
        string[] outcomes = ["correct", "incorrect", "partial", "blank", "unreadable"];
        Assert.Equal(4, MistakePassCriteria.CountMistakes(outcomes));
        Assert.Equal(expected, MistakePassCriteria.Evaluate(maximum, outcomes));
    }
}
