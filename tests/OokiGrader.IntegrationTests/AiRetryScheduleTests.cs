using OokiGrader.Host.Jobs;

namespace OokiGrader.IntegrationTests;

public sealed class AiRetryScheduleTests
{
    [Fact]
    public void DelayIsDeterministicBoundedAndGrows()
    {
        var first = AiRetrySchedule.Delay(1, "request-01");
        var repeated = AiRetrySchedule.Delay(1, "request-01");
        var fourth = AiRetrySchedule.Delay(4, "request-01");
        var capped = AiRetrySchedule.Delay(100, "request-01");

        Assert.Equal(first, repeated);
        Assert.InRange(first, TimeSpan.FromSeconds(24), TimeSpan.FromSeconds(36));
        Assert.InRange(fourth, TimeSpan.FromMinutes(24), TimeSpan.FromMinutes(36));
        Assert.InRange(capped, TimeSpan.FromMinutes(96), TimeSpan.FromMinutes(144));
    }

    [Fact]
    public void DifferentRequestKeysDoNotSynchronizeRetries()
    {
        var first = AiRetrySchedule.Delay(3, "request-01");
        var second = AiRetrySchedule.Delay(3, "request-02");

        Assert.NotEqual(first, second);
    }
}
