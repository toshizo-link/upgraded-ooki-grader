using OokiGrader.SchoolManager.Delivery;

namespace OokiGrader.SchoolManager.Tests;

public sealed class GuardianDeliveryScheduleTests
{
    [Fact]
    public void PassFailUpdatesAreLimitedToOncePerTokyoDay()
    {
        var now = new DateTimeOffset(2026, 9, 15, 6, 0, 0, TimeSpan.Zero);
        var sentEarlierSameTokyoDay =
            new DateTimeOffset(2026, 9, 15, 1, 0, 0, TimeSpan.Zero);

        var notBefore = GuardianDeliverySchedule.NextPassFailNotBefore(
            now,
            sentEarlierSameTokyoDay);

        Assert.Equal(
            new DateTimeOffset(2026, 9, 15, 15, 0, 0, TimeSpan.Zero),
            notBefore);
    }

    [Fact]
    public void FirstPassFailUpdateCanSendImmediately()
    {
        var now = new DateTimeOffset(2026, 9, 15, 6, 0, 0, TimeSpan.Zero);

        Assert.Equal(
            now,
            GuardianDeliverySchedule.NextPassFailNotBefore(now, null));
    }
}
