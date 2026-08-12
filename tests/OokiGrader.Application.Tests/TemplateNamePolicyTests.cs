using OokiGrader.Application.Templates;
using OokiGrader.Domain.Common;
using OokiGrader.Domain.Templates;

namespace OokiGrader.Application.Tests;

public sealed class TemplateNamePolicyTests
{
    [Fact]
    public void NormalizesWidthWhitespaceAndControlsWithoutTransliteration()
    {
        var normalized = TemplateNamePolicy.NormalizePrintedName(
            "\u0000　ＳＴＥＰ算数\t\t第４回\r\n ");

        Assert.Equal("STEP算数 第4回", normalized);
    }

    [Fact]
    public void PreservesJapaneseScriptAndOrdinarySubjectAndGradeText()
    {
        const string printed = "小学4年 算数 漢字とかな";

        Assert.Equal(printed, TemplateNamePolicy.NormalizePrintedName(printed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    [InlineData("\u0000\u0001")]
    public void RejectsMissingName(string? value)
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => TemplateNamePolicy.NormalizePrintedName(value));

        Assert.Contains(
            exception.Errors,
            error => error.Code == "TEST_NAME_REQUIRED");
    }

    [Fact]
    public void CapsNormalizedNameAtRepositoryTitleLimit()
    {
        var normalized = TemplateNamePolicy.NormalizePrintedName(
            new string('あ', TemplateNamePolicy.MaximumTitleLength + 20));

        Assert.Equal(TemplateNamePolicy.MaximumTitleLength, normalized.Length);
    }

    [Theory]
    [InlineData(1, "STEP算数 第4回-1")]
    [InlineData(2, "STEP算数 第4回-2")]
    [InlineData(3, "STEP算数 第4回-3")]
    public void AppendsDeterministicStepSuffixExactlyOnce(
        int variation,
        string expected)
    {
        Assert.Equal(
            expected,
            TemplateNamePolicy.CreateFinalName(
                TestType.Step,
                " STEP算数　第4回 ",
                variation));
    }

    [Theory]
    [InlineData("STEP算数-1")]
    [InlineData("STEP算数-2")]
    [InlineData("STEP算数-3")]
    [InlineData("STEP算数－１")]
    public void ExistingStepSuffixRequiresTeacherConfirmation(string baseName)
    {
        var exception = Assert.Throws<DomainValidationException>(() =>
            TemplateNamePolicy.AppendStepSuffix(baseName, 1));

        Assert.Contains(
            exception.Errors,
            error => error.Code == "STEP_NAME_ALREADY_SUFFIXED");
    }

    [Theory]
    [InlineData(TestType.Hop)]
    [InlineData(TestType.ClassPlacement)]
    [InlineData(TestType.Other)]
    public void NonStepNamesDoNotReceiveSuffix(TestType testType)
    {
        Assert.Equal(
            "算数 第4回",
            TemplateNamePolicy.CreateFinalName(testType, "算数　第4回"));
    }

    [Fact]
    public void EqualNormalizedStepNamesUseCommonBaseName()
    {
        var result = TemplateNamePolicy.EvaluateStepSetBaseNames(
            ["ＳＴＥＰ算数　第４回", "STEP算数 第4回", " STEP算数  第4回 "]);

        Assert.True(result.IsConsistent);
        Assert.False(result.RequiresUserConfirmation);
        Assert.Equal("STEP算数 第4回", result.BaseName);
        Assert.Empty(result.WarningCodes);
    }

    [Fact]
    public void MissingStepNameRequiresFinalCheckBaseName()
    {
        var result = TemplateNamePolicy.EvaluateStepSetBaseNames(
            ["STEP算数 第4回", null, "STEP算数 第4回"]);

        Assert.True(result.RequiresUserConfirmation);
        Assert.Null(result.BaseName);
        Assert.Contains("TEST_NAME_REQUIRED", result.WarningCodes);
    }

    [Fact]
    public void DifferentStepNamesRaiseMismatch()
    {
        var result = TemplateNamePolicy.EvaluateStepSetBaseNames(
            ["STEP算数 第4回", "STEP算数 第5回", "STEP算数 第4回"]);

        Assert.True(result.RequiresUserConfirmation);
        Assert.Null(result.BaseName);
        Assert.Contains("STEP_NAME_MISMATCH", result.WarningCodes);
    }

    [Fact]
    public void SuffixedExtractedStepNameRaisesBlockingWarning()
    {
        var result = TemplateNamePolicy.EvaluateStepSetBaseNames(
            ["STEP算数 第4回-1", "STEP算数 第4回-1", "STEP算数 第4回-1"]);

        Assert.True(result.RequiresUserConfirmation);
        Assert.Contains("STEP_NAME_ALREADY_SUFFIXED", result.WarningCodes);
    }

    [Fact]
    public void DuplicateDetectorUsesNormalizedFinalNames()
    {
        var duplicates = TemplateNamePolicy.FindDuplicateNames(
            ["算数　第4回", "算数 第4回", "国語 第4回"]);

        Assert.Equal(["算数 第4回"], duplicates);
        var exception = Assert.Throws<DomainValidationException>(() =>
            TemplateNamePolicy.EnsureUniqueFinalNames(
                ["算数　第4回", "算数 第4回"]));
        Assert.Contains(
            exception.Errors,
            error => error.Code == "DUPLICATE_TEMPLATE_NAME");
    }

    [Fact]
    public void StepSuffixKeepsFinalNameInsideSafeTitleLimit()
    {
        var finalName = TemplateNamePolicy.AppendStepSuffix(
            new string('あ', TemplateNamePolicy.MaximumTitleLength),
            3);

        Assert.Equal(TemplateNamePolicy.MaximumTitleLength, finalName.Length);
        Assert.EndsWith("-3", finalName, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TestType.Hop, 2, null, null, "理科6年HOP2")]
    [InlineData(TestType.Step, 4, 2, 1, "理科6年STEPセット2-1")]
    [InlineData(
        TestType.ClassPlacement,
        1,
        null,
        null,
        "理科6年クラス分けテスト")]
    public void KnownTestNamesUseOnlyTrustedBatchAndSplitInformation(
        TestType testType,
        int sequence,
        int? setIndex,
        int? variationIndex,
        string expected)
    {
        var result = TemplateNamePolicy.CreateKnownTestName(
            testType,
            "　理科　",
            GradeLevel.Grade6,
            sequence,
            setIndex,
            variationIndex);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void OtherTestCannotUseKnownTestNamePolicy()
    {
        Assert.Throws<ArgumentException>(() =>
            TemplateNamePolicy.CreateKnownTestName(
                TestType.Other,
                "理科",
                GradeLevel.Grade6,
                1));
    }

    [Fact]
    public void KnownTestNameRequiresResolvedGrade()
    {
        var exception = Assert.Throws<DomainValidationException>(() =>
            TemplateNamePolicy.CreateKnownTestName(
                TestType.Hop,
                "理科",
                GradeLevel.Unknown,
                1));

        Assert.Contains(
            exception.Errors,
            error => error.Code == GradeResolutionService.RequiredErrorCode);
    }
}
