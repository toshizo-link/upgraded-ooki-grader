using OokiGrader.Application.Templates;
using OokiGrader.Domain.Common;
using OokiGrader.Domain.Templates;

namespace OokiGrader.Application.Tests;

public sealed class GradeResolutionServiceTests
{
    [Fact]
    public void UsesFilenameWhenPaperIsAbsent()
    {
        var result = Resolve(GradeLevel.Grade4, GradeLevel.Unknown);

        AssertResolved(result, GradeLevel.Grade4, GradeEvidence.FileName);
    }

    [Fact]
    public void UsesPaperWhenFilenameIsAbsent()
    {
        var result = Resolve(GradeLevel.Unknown, GradeLevel.Grade4);

        AssertResolved(result, GradeLevel.Grade4, GradeEvidence.Paper);
    }

    [Fact]
    public void UsesCombinedEvidenceWhenBothAgree()
    {
        var result = Resolve(GradeLevel.Grade4, GradeLevel.Grade4);

        AssertResolved(
            result,
            GradeLevel.Grade4,
            GradeEvidence.FileNameAndPaper);
    }

    [Fact]
    public void RequiresUserEntryWhenBothAreAbsent()
    {
        var result = Resolve(GradeLevel.Unknown, GradeLevel.Unknown);

        Assert.False(result.IsResolved);
        Assert.True(result.RequiresUserSelection);
        Assert.Equal(GradeLevel.Unknown, result.Grade);
        Assert.Equal(GradeEvidence.None, result.Evidence);
        Assert.Equal("GRADE_REQUIRED", result.ErrorCode);
    }

    [Fact]
    public void DoesNotSilentlyChooseConflictingEvidence()
    {
        var result = Resolve(GradeLevel.Grade4, GradeLevel.Grade5);

        Assert.False(result.IsResolved);
        Assert.Equal(GradeLevel.Unknown, result.ResolvedGrade);
        Assert.Equal(GradeEvidence.None, result.Evidence);
        Assert.Equal("GRADE_CONFLICT", result.ErrorCode);
    }

    [Fact]
    public void PreservesFilenameInternalConflictCode()
    {
        var result = GradeResolutionService.Resolve(
            new FileNameGradeResult(
                GradeLevel.Unknown,
                IsUnambiguous: false,
                MatchedToken: null,
                ErrorCode: "FILENAME_GRADE_CONFLICT"),
            Paper(GradeLevel.Grade4),
            userSelection: null);

        Assert.False(result.IsResolved);
        Assert.Equal("FILENAME_GRADE_CONFLICT", result.ErrorCode);
    }

    [Theory]
    [InlineData(GradeLevel.Grade1)]
    [InlineData(GradeLevel.Grade3)]
    [InlineData(GradeLevel.Grade6)]
    public void UserSelectionIsAuthoritativeEvenWhenEvidenceConflicts(
        GradeLevel selection)
    {
        var result = GradeResolutionService.Resolve(
            File(GradeLevel.Grade4),
            Paper(GradeLevel.Grade5),
            selection);

        AssertResolved(result, selection, GradeEvidence.User);
    }

    [Fact]
    public void UnknownCannotBeUsedAsAuthoritativeUserSelection()
    {
        var exception = Assert.Throws<DomainValidationException>(() =>
            GradeResolutionService.Resolve(
                File(GradeLevel.Unknown),
                Paper(GradeLevel.Unknown),
                GradeLevel.Unknown));

        Assert.Contains(
            exception.Errors,
            error => error.Code == "GRADE_INVALID");
    }

    private static GradeResolution Resolve(
        GradeLevel filename,
        GradeLevel paper) =>
        GradeResolutionService.Resolve(
            File(filename),
            Paper(paper),
            userSelection: null);

    private static FileNameGradeResult File(GradeLevel grade) =>
        new(
            grade,
            IsUnambiguous: grade is not GradeLevel.Unknown,
            MatchedToken: grade is GradeLevel.Unknown ? null : "token",
            ErrorCode: null);

    private static PaperGradeResult Paper(GradeLevel grade) =>
        new(
            grade,
            IsUnambiguous: grade is not GradeLevel.Unknown,
            PrintedLabel: grade is GradeLevel.Unknown ? null : "label");

    private static void AssertResolved(
        GradeResolution result,
        GradeLevel grade,
        GradeEvidence evidence)
    {
        Assert.True(result.IsResolved);
        Assert.False(result.RequiresUserSelection);
        Assert.Equal(grade, result.Grade);
        Assert.Equal(grade, result.ResolvedGrade);
        Assert.Equal(evidence, result.Evidence);
        Assert.Null(result.ErrorCode);
    }
}
