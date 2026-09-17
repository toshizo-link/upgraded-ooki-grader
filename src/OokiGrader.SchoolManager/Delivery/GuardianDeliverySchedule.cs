namespace OokiGrader.SchoolManager.Delivery;

public static class GuardianDeliverySchedule
{
    private static readonly TimeZoneInfo Tokyo = ResolveTokyoTimeZone();

    public static DateTimeOffset NextPassFailNotBefore(
        DateTimeOffset now,
        DateTimeOffset? mostRecentSentAt)
    {
        if (mostRecentSentAt is null)
        {
            return now;
        }

        var localNow = TimeZoneInfo.ConvertTime(now, Tokyo);
        var localSent = TimeZoneInfo.ConvertTime(mostRecentSentAt.Value, Tokyo);
        if (localNow.Date != localSent.Date)
        {
            return now;
        }

        var nextLocalDate = DateOnly.FromDateTime(localNow.Date).AddDays(1);
        var nextLocalMidnight = new DateTime(
            nextLocalDate.Year,
            nextLocalDate.Month,
            nextLocalDate.Day,
            0,
            0,
            0,
            DateTimeKind.Unspecified);
        var offset = Tokyo.GetUtcOffset(nextLocalMidnight);
        return new DateTimeOffset(nextLocalMidnight, offset).ToUniversalTime();
    }

    public static DateOnly TokyoDate(DateTimeOffset timestamp)
    {
        var local = TimeZoneInfo.ConvertTime(timestamp, Tokyo);
        return DateOnly.FromDateTime(local.Date);
    }

    private static TimeZoneInfo ResolveTokyoTimeZone()
    {
        foreach (var id in new[] { "Tokyo Standard Time", "Asia/Tokyo" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
        }

        throw new TimeZoneNotFoundException("The Tokyo time zone is unavailable.");
    }
}
