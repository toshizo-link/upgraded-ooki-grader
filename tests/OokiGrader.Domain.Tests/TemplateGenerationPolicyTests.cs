using System.Text.Json;
using OokiGrader.Domain.Common;
using OokiGrader.Domain.Templates;

namespace OokiGrader.Domain.Tests;

public sealed class TemplateGenerationPolicyTests
{
    private readonly TemplateUnitPlanner _planner = new();

    [Theory]
    [InlineData(TestType.Hop, null, TemplatePromptSystem.Standard)]
    [InlineData(TestType.Step, null, TemplatePromptSystem.Standard)]
    [InlineData(
        TestType.ClassPlacement,
        null,
        TemplatePromptSystem.ClassPlacement)]
    [InlineData(
        TestType.Other,
        AnswerStyle.Normal,
        TemplatePromptSystem.Standard)]
    [InlineData(
        TestType.Other,
        AnswerStyle.FillBlank,
        TemplatePromptSystem.FillBlank)]
    public void PromptRouterImplementsEveryRoutingTableRow(
        TestType testType,
        AnswerStyle? answerStyle,
        TemplatePromptSystem expected)
    {
        Assert.Equal(expected, TemplatePromptRouter.Resolve(testType, answerStyle));
    }

    [Fact]
    public void OtherRequiresAnswerStyle()
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => TemplatePromptRouter.Resolve(TestType.Other, null));

        Assert.Contains(
            exception.Errors,
            error => error.Code == "ANSWER_STYLE_REQUIRED"
                && error.Path == "answerStyle");
    }

    [Theory]
    [InlineData(TestType.Hop, AnswerStyle.Normal)]
    [InlineData(TestType.Hop, AnswerStyle.FillBlank)]
    [InlineData(TestType.Step, AnswerStyle.Normal)]
    [InlineData(TestType.Step, AnswerStyle.FillBlank)]
    [InlineData(TestType.ClassPlacement, AnswerStyle.Normal)]
    [InlineData(TestType.ClassPlacement, AnswerStyle.FillBlank)]
    public void NonOtherTypesRejectAnswerStyle(
        TestType testType,
        AnswerStyle answerStyle)
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => TemplatePromptRouter.Resolve(testType, answerStyle));

        Assert.Contains(
            exception.Errors,
            error => error.Code == "ANSWER_STYLE_NOT_ALLOWED");
    }

    [Fact]
    public void HopPlansExactlyOneUnitPerPage()
    {
        var units = _planner.Plan(TestType.Hop, 4);

        Assert.Collection(
            units,
            unit => AssertUnit(unit, 1, 1, 1),
            unit => AssertUnit(unit, 2, 2, 2),
            unit => AssertUnit(unit, 3, 3, 3),
            unit => AssertUnit(unit, 4, 4, 4));
        Assert.All(units, unit =>
        {
            Assert.Equal(unit.FirstPage, unit.LastPage);
            Assert.Null(unit.StepSetIndex);
            Assert.Null(unit.StepVariationIndex);
            Assert.Null(unit.DeterministicSuffix);
        });
    }

    [Fact]
    public void SixPageStepPlansThreeIndependentTwoPageUnits()
    {
        var units = _planner.Plan(TestType.Step, 6);

        Assert.Collection(
            units,
            unit => AssertStepUnit(unit, 1, 1, 2, 1, 1, "-1"),
            unit => AssertStepUnit(unit, 2, 3, 4, 1, 2, "-2"),
            unit => AssertStepUnit(unit, 3, 5, 6, 1, 3, "-3"));
    }

    [Fact]
    public void TwelvePageStepResetsSuffixesForSecondSet()
    {
        var units = _planner.Plan(TestType.Step, 12);

        Assert.Collection(
            units,
            unit => AssertStepUnit(unit, 1, 1, 2, 1, 1, "-1"),
            unit => AssertStepUnit(unit, 2, 3, 4, 1, 2, "-2"),
            unit => AssertStepUnit(unit, 3, 5, 6, 1, 3, "-3"),
            unit => AssertStepUnit(unit, 4, 7, 8, 2, 1, "-1"),
            unit => AssertStepUnit(unit, 5, 9, 10, 2, 2, "-2"),
            unit => AssertStepUnit(unit, 6, 11, 12, 2, 3, "-3"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(13)]
    [InlineData(17)]
    [InlineData(25)]
    public void StepRejectsEveryNonMultipleOfSix(int pageCount)
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => _planner.Plan(TestType.Step, pageCount));

        Assert.Contains(
            exception.Errors,
            error => error.Code
                == "STEP_PAGE_COUNT_NOT_DIVISIBLE_BY_SIX"
                && error.Path == "pageCount");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PlannerRejectsDocumentsWithoutPositivePageCount(int pageCount)
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => _planner.Plan(TestType.Hop, pageCount));

        Assert.Contains(
            exception.Errors,
            error => error.Code == "PDF_PAGE_COUNT_INVALID");
    }

    [Theory]
    [InlineData(TestType.ClassPlacement)]
    [InlineData(TestType.Other)]
    public void WholeDocumentTypesPlanOneUnit(TestType testType)
    {
        var unit = Assert.Single(_planner.Plan(testType, 9));

        AssertUnit(unit, sequence: 1, firstPage: 1, lastPage: 9);
        Assert.Null(unit.StepSetIndex);
        Assert.Null(unit.StepVariationIndex);
        Assert.Null(unit.DeterministicSuffix);
    }

    [Fact]
    public void UnitPlanRejectsSuffixThatDoesNotMatchVariation()
    {
        Assert.Throws<ArgumentException>(() => new TemplateUnitPlan(
            sequence: 1,
            firstPage: 1,
            lastPage: 2,
            stepSetIndex: 1,
            stepVariationIndex: 2,
            deterministicSuffix: "-1"));
    }

    [Fact]
    public void UnitPlanRejectsStepRangeThatIsNotExactlyTwoPages()
    {
        Assert.Throws<ArgumentException>(() => new TemplateUnitPlan(
            sequence: 1,
            firstPage: 1,
            lastPage: 3,
            stepSetIndex: 1,
            stepVariationIndex: 1,
            deterministicSuffix: "-1"));
    }

    [Fact]
    public void GenerationProfileHashIsDeterministicAndCoversEveryField()
    {
        var profile = Profile();

        var first = profile.ComputeHash();
        var second = profile.ComputeHash();

        Assert.Equal(first, second);
        Assert.Matches("^[0-9a-f]{64}$", first);
        Assert.NotEqual(
            first,
            (profile with { DeterministicSuffix = "-2" }).ComputeHash());
        Assert.NotEqual(
            first,
            (profile with { Subject = "国語" }).ComputeHash());
        Assert.NotEqual(
            first,
            (profile with { ExtractionSchemaVersion = "v-next" }).ComputeHash());
    }

    [Theory]
    [InlineData(TestType.Hop, "\"hop\"")]
    [InlineData(TestType.Step, "\"step\"")]
    [InlineData(TestType.ClassPlacement, "\"classPlacement\"")]
    [InlineData(TestType.Other, "\"other\"")]
    public void TestTypeUsesStableStringJson(TestType value, string expectedJson)
    {
        Assert.Equal(expectedJson, JsonSerializer.Serialize(value));
    }

    [Theory]
    [InlineData(AnswerStyle.Normal, "\"normal\"")]
    [InlineData(AnswerStyle.FillBlank, "\"fillBlank\"")]
    public void AnswerStyleUsesStableStringJson(
        AnswerStyle value,
        string expectedJson)
    {
        Assert.Equal(expectedJson, JsonSerializer.Serialize(value));
    }

    [Theory]
    [InlineData(GradeLevel.Unknown, "\"unknown\"")]
    [InlineData(GradeLevel.Grade1, "\"grade1\"")]
    [InlineData(GradeLevel.Grade6, "\"grade6\"")]
    public void GradeLevelUsesStableStringJson(
        GradeLevel value,
        string expectedJson)
    {
        Assert.Equal(expectedJson, JsonSerializer.Serialize(value));
    }

    private static TemplateGenerationProfile Profile() =>
        new(
            TemplateGenerationProfile.CurrentProfileVersion,
            TestType.Step,
            "算数",
            AnswerStyle: null,
            TemplatePromptSystem.Standard,
            SourcePageCount: 6,
            UnitSequence: 1,
            FirstPage: 1,
            LastPage: 2,
            StepSetIndex: 1,
            StepVariationIndex: 1,
            DeterministicSuffix: "-1",
            TemplateGenerationProfile.CurrentSplitPolicyVersion,
            TemplateGenerationProfile.CurrentNamingPolicyVersion,
            ExtractionPromptVersion: "template-extract-v2.0.0",
            ExtractionSchemaVersion: "template_extract_v5");

    private static void AssertUnit(
        TemplateUnitPlan unit,
        int sequence,
        int firstPage,
        int lastPage)
    {
        Assert.Equal(sequence, unit.Sequence);
        Assert.Equal(firstPage, unit.FirstPage);
        Assert.Equal(lastPage, unit.LastPage);
    }

    private static void AssertStepUnit(
        TemplateUnitPlan unit,
        int sequence,
        int firstPage,
        int lastPage,
        int setIndex,
        int variationIndex,
        string suffix)
    {
        AssertUnit(unit, sequence, firstPage, lastPage);
        Assert.Equal(2, unit.LastPage - unit.FirstPage + 1);
        Assert.Equal(setIndex, unit.StepSetIndex);
        Assert.Equal(variationIndex, unit.StepVariationIndex);
        Assert.Equal(suffix, unit.DeterministicSuffix);
    }
}
