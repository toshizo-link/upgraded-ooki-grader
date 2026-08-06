using OokiGrader.Domain.Common;

namespace OokiGrader.Domain.Retention;

public static class CalendarMonthRetention
{
    public const int DefaultScanRetentionMonths = 3;

    public static DateOnly CalculateCutoff(
        DateOnly localDate,
        int calendarMonths = DefaultScanRetentionMonths)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(calendarMonths);

        return localDate.AddMonths(-calendarMonths);
    }

    public static bool IsPastCutoff(
        DateOnly uploadCompletionDate,
        DateOnly localDate,
        int calendarMonths = DefaultScanRetentionMonths) =>
        uploadCompletionDate < CalculateCutoff(localDate, calendarMonths);

    public static DateTimeOffset CalculateCutoffInstant(
        DateTimeOffset now,
        TimeZoneInfo siteTimeZone,
        int calendarMonths = DefaultScanRetentionMonths)
    {
        ArgumentNullException.ThrowIfNull(siteTimeZone);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(calendarMonths);

        var localNow = TimeZoneInfo.ConvertTime(now, siteTimeZone);
        var localCutoffWallTime = DateTime.SpecifyKind(
            localNow.DateTime.AddMonths(-calendarMonths),
            DateTimeKind.Unspecified);
        if (siteTimeZone.IsInvalidTime(localCutoffWallTime))
        {
            throw new DomainValidationException(
            [
                new DomainError(
                    "retention.invalid_local_cutoff",
                    "The calendar-month cutoff falls in an invalid local clock interval."),
            ]);
        }

        var utcCutoff = TimeZoneInfo.ConvertTimeToUtc(
            localCutoffWallTime,
            siteTimeZone);
        return new DateTimeOffset(utcCutoff, TimeSpan.Zero);
    }

    public static bool IsPastCutoff(
        DateTimeOffset uploadCompletionInstant,
        DateTimeOffset now,
        TimeZoneInfo siteTimeZone,
        int calendarMonths = DefaultScanRetentionMonths) =>
        uploadCompletionInstant.ToUniversalTime()
        < CalculateCutoffInstant(now, siteTimeZone, calendarMonths);
}

public enum ManagedQuotaState
{
    Healthy,
    Warning,
    CleanupRequired,
    HardLimit,
}

public enum PhysicalFreeSpaceState
{
    Healthy,
    Warning,
    Critical,
}

public sealed record StorageAdmissionResult(
    bool IsAllowed,
    ManagedQuotaState ManagedQuotaState,
    PhysicalFreeSpaceState PhysicalFreeSpaceState,
    long ProjectedManagedBytes,
    long RequiredPhysicalBytes,
    string Reason);

public sealed class StorageQuotaPolicy
{
    public const long Gibibyte = 1024L * 1024L * 1024L;
    public const long DefaultManagedHardLimitBytes = 150L * Gibibyte;
    public const long DefaultManagedCleanupTargetBytes = 145L * Gibibyte;
    public const long DefaultManagedWarningBytes = 135L * Gibibyte;
    public const long DefaultPhysicalWarningBytes = 15L * Gibibyte;
    public const long DefaultPhysicalReserveBytes = 5L * Gibibyte;

    public StorageQuotaPolicy(
        long managedHardLimitBytes = DefaultManagedHardLimitBytes,
        long managedCleanupTargetBytes = DefaultManagedCleanupTargetBytes,
        long managedWarningBytes = DefaultManagedWarningBytes,
        long physicalWarningBytes = DefaultPhysicalWarningBytes,
        long physicalReserveBytes = DefaultPhysicalReserveBytes)
    {
        if (managedWarningBytes <= 0
            || managedCleanupTargetBytes <= managedWarningBytes
            || managedHardLimitBytes <= managedCleanupTargetBytes)
        {
            throw new DomainValidationException(
            [
                new DomainError(
                    "quota.invalid_managed_thresholds",
                    "Managed thresholds must satisfy 0 < warning < cleanup target < hard limit."),
            ]);
        }

        if (physicalReserveBytes <= 0 || physicalWarningBytes <= physicalReserveBytes)
        {
            throw new DomainValidationException(
            [
                new DomainError(
                    "quota.invalid_physical_thresholds",
                    "Physical thresholds must satisfy 0 < reserve < warning."),
            ]);
        }

        ManagedHardLimitBytes = managedHardLimitBytes;
        ManagedCleanupTargetBytes = managedCleanupTargetBytes;
        ManagedWarningBytes = managedWarningBytes;
        PhysicalWarningBytes = physicalWarningBytes;
        PhysicalReserveBytes = physicalReserveBytes;
    }

    public long ManagedHardLimitBytes { get; }

    public long ManagedCleanupTargetBytes { get; }

    public long ManagedWarningBytes { get; }

    public long PhysicalWarningBytes { get; }

    public long PhysicalReserveBytes { get; }

    public ManagedQuotaState EvaluateManagedBytes(long managedBytes)
    {
        EnsureNonNegative(managedBytes, nameof(managedBytes));

        if (managedBytes >= ManagedHardLimitBytes)
        {
            return ManagedQuotaState.HardLimit;
        }

        if (managedBytes >= ManagedCleanupTargetBytes)
        {
            return ManagedQuotaState.CleanupRequired;
        }

        return managedBytes >= ManagedWarningBytes
            ? ManagedQuotaState.Warning
            : ManagedQuotaState.Healthy;
    }

    public PhysicalFreeSpaceState EvaluatePhysicalFreeBytes(long physicalFreeBytes)
    {
        EnsureNonNegative(physicalFreeBytes, nameof(physicalFreeBytes));

        if (physicalFreeBytes < PhysicalReserveBytes)
        {
            return PhysicalFreeSpaceState.Critical;
        }

        return physicalFreeBytes < PhysicalWarningBytes
            ? PhysicalFreeSpaceState.Warning
            : PhysicalFreeSpaceState.Healthy;
    }

    public StorageAdmissionResult EvaluateAdmission(
        long currentManagedBytes,
        long remainingUploadBytes,
        long estimatedRasterExpansionBytes,
        long temporaryAllowanceBytes,
        long physicalFreeBytes)
    {
        EnsureNonNegative(currentManagedBytes, nameof(currentManagedBytes));
        EnsureNonNegative(remainingUploadBytes, nameof(remainingUploadBytes));
        EnsureNonNegative(
            estimatedRasterExpansionBytes,
            nameof(estimatedRasterExpansionBytes));
        EnsureNonNegative(temporaryAllowanceBytes, nameof(temporaryAllowanceBytes));
        EnsureNonNegative(physicalFreeBytes, nameof(physicalFreeBytes));

        long projectedManaged;
        long requiredPhysical;
        try
        {
            projectedManaged = checked(
                currentManagedBytes
                + remainingUploadBytes
                + estimatedRasterExpansionBytes);
            requiredPhysical = checked(
                remainingUploadBytes
                + estimatedRasterExpansionBytes
                + temporaryAllowanceBytes
                + PhysicalReserveBytes);
        }
        catch (OverflowException)
        {
            return new StorageAdmissionResult(
                false,
                ManagedQuotaState.HardLimit,
                PhysicalFreeSpaceState.Critical,
                long.MaxValue,
                long.MaxValue,
                "The storage estimate overflowed the supported byte range.");
        }

        var managedState = EvaluateManagedBytes(projectedManaged);
        var physicalState = EvaluatePhysicalFreeBytes(physicalFreeBytes);
        var allowed = managedState != ManagedQuotaState.HardLimit
            && physicalFreeBytes >= requiredPhysical;

        return new StorageAdmissionResult(
            allowed,
            managedState,
            physicalState,
            projectedManaged,
            requiredPhysical,
            allowed
                ? "Storage admission thresholds are satisfied."
                : managedState == ManagedQuotaState.HardLimit
                    ? "Projected managed scan bytes reach or exceed the hard limit."
                    : "Physical free space would breach the emergency reserve.");
    }

    private static void EnsureNonNegative(long value, string parameterName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
