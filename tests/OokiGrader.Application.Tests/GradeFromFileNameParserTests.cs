using OokiGrader.Application.Templates;
using OokiGrader.Domain.Templates;

namespace OokiGrader.Application.Tests;

public sealed class GradeFromFileNameParserTests
{
    public static TheoryData<string, GradeLevel> ExplicitGradeForms()
    {
        var data = new TheoryData<string, GradeLevel>();
        for (var grade = 1; grade <= 6; grade++)
        {
            var expected = (GradeLevel)grade;
            data.Add($"小{grade}_算数.pdf", expected);
            data.Add($"小学{grade}年_算数.pdf", expected);
            data.Add($"小学{grade}年生_算数.pdf", expected);
            data.Add($"{grade}年_算数.pdf", expected);
            data.Add($"{grade}年生_算数.pdf", expected);
            data.Add($"G{grade}_math.pdf", expected);
            data.Add($"Grade {grade}_math.pdf", expected);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ExplicitGradeForms))]
    public void RecognizesEveryAcceptedExplicitForm(
        string fileName,
        GradeLevel expected)
    {
        var result = GradeFromFileNameParser.Parse(fileName);

        Assert.True(result.IsUnambiguous);
        Assert.Equal(expected, result.Grade);
        Assert.NotNull(result.MatchedToken);
        Assert.Null(result.ErrorCode);
    }

    [Theory]
    [InlineData("小１_算数.pdf", GradeLevel.Grade1)]
    [InlineData("小学４年生_STEP.pdf", GradeLevel.Grade4)]
    [InlineData("算数4年生STEP.pdf", GradeLevel.Grade4)]
    [InlineData("Ｇ６_国語.pdf", GradeLevel.Grade6)]
    [InlineData("Ｇｒａｄｅ　３_理科.pdf", GradeLevel.Grade3)]
    public void NormalizesFullWidthForms(
        string fileName,
        GradeLevel expected)
    {
        Assert.Equal(expected, GradeFromFileNameParser.Parse(fileName).Grade);
    }

    [Theory]
    [InlineData("1.pdf")]
    [InlineData("2026-08-09_算数.pdf")]
    [InlineData("2026年08月09日_算数.pdf")]
    [InlineData("令和6年_算数.pdf")]
    [InlineData("6年8月9日_算数.pdf")]
    [InlineData("R6年_算数.pdf")]
    [InlineData("第1回_STEP算数.pdf")]
    [InlineData("STEP算数-1.pdf")]
    [InlineData("page_4.pdf")]
    [InlineData("4ページ.pdf")]
    [InlineData("2組_算数.pdf")]
    [InlineData("100点_算数.pdf")]
    [InlineData("STEP算数.pdf")]
    public void DoesNotInferGradeFromUnrelatedNumbers(string fileName)
    {
        var result = GradeFromFileNameParser.Parse(fileName);

        Assert.Equal(GradeLevel.Unknown, result.Grade);
        Assert.False(result.IsUnambiguous);
        Assert.Null(result.MatchedToken);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void SameGradeRepeatedInDifferentExplicitFormsRemainsUnambiguous()
    {
        var result = GradeFromFileNameParser.Parse(
            "小4_Grade 4_STEP算数.pdf");

        Assert.True(result.IsUnambiguous);
        Assert.Equal(GradeLevel.Grade4, result.Grade);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void ConflictingExplicitTokensReturnBlockingConflict()
    {
        var result = GradeFromFileNameParser.Parse(
            "小4_Grade 5_STEP算数.pdf");

        Assert.Equal(GradeLevel.Unknown, result.Grade);
        Assert.False(result.IsUnambiguous);
        Assert.Null(result.MatchedToken);
        Assert.Equal("FILENAME_GRADE_CONFLICT", result.ErrorCode);
    }
}
