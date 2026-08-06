using OokiGrader.Ai.Abstractions;
using OokiGrader.Host.Api;

namespace OokiGrader.IntegrationTests;

public sealed class AiProfileValidationTests
{
    [Fact]
    public void NewInitialGradingProfilesUseTheStandardQueue()
    {
        Assert.Equal(
            "queued_standard",
            AiAdminEndpoints.DefaultProcessingStrategy(
                AiTaskTypes.InitialGrading));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("expedite_standard")]
    [InlineData("queued_standard")]
    public void InitialGradingAcceptsStandardApiStrategies(string? strategy)
    {
        Assert.True(AiAdminEndpoints.IsProcessingStrategyValid(
            AiTaskTypes.InitialGrading,
            strategy));
    }

    [Fact]
    public void NewInitialGradingProfilesRejectLegacyGeminiBatch()
    {
        Assert.False(AiAdminEndpoints.IsProcessingStrategyValid(
            AiTaskTypes.InitialGrading,
            "gemini_batch"));
    }

    [Theory]
    [InlineData(AiTaskTypes.TemplateExtraction)]
    [InlineData(AiTaskTypes.NameTranscription)]
    [InlineData(AiTaskTypes.Adjudication)]
    public void NonInitialTasksRejectGeminiBatch(string taskType)
    {
        Assert.False(AiAdminEndpoints.IsProcessingStrategyValid(
            taskType,
            "gemini_batch"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("openrouter_queued")]
    [InlineData("unbounded")]
    public void UnknownStrategiesAreRejected(string strategy)
    {
        Assert.False(AiAdminEndpoints.IsProcessingStrategyValid(
            AiTaskTypes.InitialGrading,
            strategy));
    }
}
