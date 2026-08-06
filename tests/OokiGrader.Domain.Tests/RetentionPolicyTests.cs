using OokiGrader.Domain.Retention;

namespace OokiGrader.Domain.Tests;

public sealed class RetentionPolicyTests
{
    [Theory]
    [InlineData(2025, 5, 31, 2025, 2, 28)]
    [InlineData(2024, 5, 31, 2024, 2, 29)]
    [InlineData(2026, 1, 31, 2025, 10, 31)]
    [InlineData(2026, 3, 30, 2025, 12, 30)]
    public void ThreeCalendarMonthCutoffClampsCalendarDates(
        int year,
        int month,
        int day,
        int expectedYear,
        int expectedMonth,
        int expectedDay)
    {
        var cutoff = CalendarMonthRetention.CalculateCutoff(
            new DateOnly(year, month, day));

        Assert.Equal(
            new DateOnly(expectedYear, expectedMonth, expectedDay),
            cutoff);
    }

    [Fact]
    public void ExactCutoffIsNotPastCutoff()
    {
        var localDate = new DateOnly(2025, 5, 31);
        var cutoff = new DateOnly(2025, 2, 28);

        Assert.False(CalendarMonthRetention.IsPastCutoff(cutoff, localDate));
        Assert.True(
            CalendarMonthRetention.IsPastCutoff(
                cutoff.AddDays(-1),
                localDate));
    }

    [Fact]
    public void InstantCutoffUsesSiteCalendarAndStrictComparison()
    {
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
        var now = new DateTimeOffset(
            2025,
            5,
            31,
            3,
            0,
            0,
            TimeSpan.FromHours(9));
        var expectedCutoff = new DateTimeOffset(
            2025,
            2,
            28,
            3,
            0,
            0,
            TimeSpan.FromHours(9)).ToUniversalTime();

        var cutoff = CalendarMonthRetention.CalculateCutoffInstant(now, tokyo);

        Assert.Equal(expectedCutoff, cutoff);
        Assert.False(CalendarMonthRetention.IsPastCutoff(cutoff, now, tokyo));
        Assert.True(
            CalendarMonthRetention.IsPastCutoff(
                cutoff.AddTicks(-1),
                now,
                tokyo));
    }

    [Theory]
    [InlineData(134, ManagedQuotaState.Healthy)]
    [InlineData(135, ManagedQuotaState.Warning)]
    [InlineData(144, ManagedQuotaState.Warning)]
    [InlineData(145, ManagedQuotaState.CleanupRequired)]
    [InlineData(149, ManagedQuotaState.CleanupRequired)]
    [InlineData(150, ManagedQuotaState.HardLimit)]
    public void ManagedQuotaThresholdsAreDeterministic(
        long gibibytes,
        ManagedQuotaState expected)
    {
        var policy = new StorageQuotaPolicy();

        Assert.Equal(
            expected,
            policy.EvaluateManagedBytes(
                gibibytes * StorageQuotaPolicy.Gibibyte));
    }

    [Theory]
    [InlineData(15, PhysicalFreeSpaceState.Healthy)]
    [InlineData(14, PhysicalFreeSpaceState.Warning)]
    [InlineData(5, PhysicalFreeSpaceState.Warning)]
    [InlineData(4, PhysicalFreeSpaceState.Critical)]
    public void PhysicalFreeThresholdsPreserveEmergencyReserve(
        long gibibytes,
        PhysicalFreeSpaceState expected)
    {
        var policy = new StorageQuotaPolicy();

        Assert.Equal(
            expected,
            policy.EvaluatePhysicalFreeBytes(
                gibibytes * StorageQuotaPolicy.Gibibyte));
    }

    [Fact]
    public void AdmissionBlocksProjectedHardQuota()
    {
        var gib = StorageQuotaPolicy.Gibibyte;
        var result = new StorageQuotaPolicy().EvaluateAdmission(
            currentManagedBytes: 149 * gib,
            remainingUploadBytes: 1 * gib,
            estimatedRasterExpansionBytes: 0,
            temporaryAllowanceBytes: 0,
            physicalFreeBytes: 100 * gib);

        Assert.False(result.IsAllowed);
        Assert.Equal(ManagedQuotaState.HardLimit, result.ManagedQuotaState);
    }

    [Fact]
    public void AdmissionIncludesRasterTempAndFiveGibReserve()
    {
        var gib = StorageQuotaPolicy.Gibibyte;
        var result = new StorageQuotaPolicy().EvaluateAdmission(
            currentManagedBytes: 10 * gib,
            remainingUploadBytes: 1 * gib,
            estimatedRasterExpansionBytes: 2 * gib,
            temporaryAllowanceBytes: 1 * gib,
            physicalFreeBytes: 8 * gib);

        Assert.False(result.IsAllowed);
        Assert.Equal(9 * gib, result.RequiredPhysicalBytes);
    }
}
