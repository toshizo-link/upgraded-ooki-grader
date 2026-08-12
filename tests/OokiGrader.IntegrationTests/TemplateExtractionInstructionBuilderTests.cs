using OokiGrader.Domain.Templates;
using OokiGrader.Host.Jobs;

namespace OokiGrader.IntegrationTests;

public sealed class TemplateExtractionInstructionBuilderTests
{
    [Theory]
    [InlineData(TestType.Hop, null, TemplatePromptSystem.Standard, "system-1-standard-v1")]
    [InlineData(TestType.Step, null, TemplatePromptSystem.Standard, "system-1-standard-v1")]
    [InlineData(TestType.ClassPlacement, null, TemplatePromptSystem.ClassPlacement, "system-2-class-placement-v1")]
    [InlineData(TestType.Other, AnswerStyle.Normal, TemplatePromptSystem.Standard, "system-1-standard-v1")]
    [InlineData(TestType.Other, AnswerStyle.FillBlank, TemplatePromptSystem.FillBlank, "system-3-fill-blank-v1")]
    public void SelectsOnlyServerRoutedPromptFragment(
        TestType testType,
        AnswerStyle? answerStyle,
        TemplatePromptSystem promptSystem,
        string expectedFragment)
    {
        var profile = Profile(testType, answerStyle, promptSystem);

        var built = TemplateExtractionInstructionBuilder.Build(
            "request-1",
            "unit-1",
            profile,
            rotationsWereApplied: false);

        Assert.Contains("orientation-gate-v1", built.UserInstruction);
        Assert.Contains("common-extraction-core-v2", built.UserInstruction);
        Assert.Contains(expectedFragment, built.UserInstruction);
        Assert.Contains("paper-name-and-grade-v1", built.UserInstruction);
        Assert.DoesNotContain("display_name", built.UserInstruction,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("current_metadata", built.UserInstruction,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("detect test type", built.UserInstruction,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(64, built.Fingerprint.Length);
    }

    [Fact]
    public void StepManifestContainsExactlyTwoLocalPagesAndOriginalRange()
    {
        var built = TemplateExtractionInstructionBuilder.Build(
            "request-1",
            "unit-1",
            Profile(TestType.Step, null, TemplatePromptSystem.Standard),
            rotationsWereApplied: true);

        Assert.Equal(2, built.Pages.Count);
        Assert.Equal([1, 2], built.Pages.Select(page => page.PageNumber));
        Assert.Contains("\"original_first_page\":3", built.UserInstruction);
        Assert.Contains("\"original_last_page\":4", built.UserInstruction);
        Assert.Contains("\"host_applied_requested_rotations\":true",
            built.UserInstruction);
    }

    private static TemplateGenerationProfile Profile(
        TestType testType,
        AnswerStyle? answerStyle,
        TemplatePromptSystem promptSystem) => new(
            1,
            testType,
            "算数",
            answerStyle,
            promptSystem,
            SourcePageCount: testType == TestType.Step ? 6 : 1,
            UnitSequence: 2,
            FirstPage: testType == TestType.Step ? 3 : 1,
            LastPage: testType == TestType.Step ? 4 : 1,
            StepSetIndex: testType == TestType.Step ? 1 : null,
            StepVariationIndex: testType == TestType.Step ? 2 : null,
            DeterministicSuffix: testType == TestType.Step ? "-2" : null,
            TemplateGenerationProfile.CurrentSplitPolicyVersion,
            TemplateGenerationProfile.CurrentNamingPolicyVersion,
            "template-extract-v2.0.0",
            "template_extract_v5");
}
