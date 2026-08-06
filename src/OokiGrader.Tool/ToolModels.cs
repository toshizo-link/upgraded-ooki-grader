namespace OokiGrader.Tool;

public static class ToolExitCodes
{
    public const int Success = 0;
    public const int Usage = 2;
    public const int CheckFailed = 3;
    public const int SafetyRefusal = 4;
}

public sealed record ToolComponentResult(
    string State,
    string? ErrorCode = null,
    string? Detail = null);

public sealed record DatabaseHealthResult(
    string State,
    string IntegrityResult,
    string? CurrentMigrationId,
    string? ExpectedMigrationId,
    bool SchemaCurrent,
    bool MaintenanceMode,
    bool ConfiguredDataRootMatches,
    long? PhysicalFreeReserveBytes,
    DateTimeOffset? LastVerifiedBackupAt,
    string? ErrorCode = null);

public sealed record StorageHealthResult(
    string State,
    bool DataRootReadable,
    bool ContentRootReadable,
    bool WriteProbePerformed,
    long? AvailableFreeBytes,
    long? RequiredReserveBytes,
    bool ReserveSatisfied,
    bool RestoreOrMigrationMarkerPresent,
    string? ErrorCode = null);

public sealed record HealthCommandResult(
    string Command,
    string State,
    DateTimeOffset CheckedAt,
    bool MutationPerformed,
    DatabaseHealthResult Database,
    StorageHealthResult Storage);

public sealed record BackupVerifyCommandResult(
    string Command,
    string State,
    DateTimeOffset CheckedAt,
    bool MutationPerformed,
    string BackupId,
    bool Verified,
    string IntegrityResult,
    int VerifiedFileCount,
    long VerifiedBytes,
    string? DatabaseMigrationId,
    string? ErrorCode,
    string? Detail);

public sealed record RestorePlanCommandResult(
    string Command,
    string State,
    DateTimeOffset CheckedAt,
    bool MutationPerformed,
    string OperationMode,
    bool LiveDataOverwriteSupported,
    bool MaintenanceConfirmationRequired,
    bool OfflineConfirmationRequired,
    string BackupId,
    bool CanRestore,
    string IntegrityResult,
    string? BackupMigrationId,
    string? CurrentMigrationId,
    bool RequiresMigration,
    bool ManagedScansIncluded,
    IReadOnlyList<string> RequiredActions,
    string? ErrorCode,
    string? Detail);

public sealed record RestoreExecuteCommandResult(
    string Command,
    string State,
    DateTimeOffset CompletedAt,
    bool MutationPerformed,
    string OperationMode,
    string BackupId,
    string ManifestSha256,
    int RestoredFileCount,
    long RestoredBytes,
    bool RollbackSnapshotCreated,
    string RollbackSnapshotId,
    bool MaintenanceModeEnforced,
    bool RestoreMarkerPresent,
    bool ManagedScansIncluded,
    bool ProviderCredentialsRequireValidation,
    IReadOnlyList<string> RequiredActions);

public sealed record ToolErrorResult(
    string Command,
    string State,
    bool MutationPerformed,
    string ErrorCode,
    string Detail);
