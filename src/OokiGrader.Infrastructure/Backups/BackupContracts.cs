using System.Text.Json.Serialization;

namespace OokiGrader.Infrastructure.Backups;

public static class BackupStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Verifying = "verifying";
    public const string Verified = "verified";
    public const string Failed = "failed";
    public const string Expired = "expired";
}

public sealed record BackupCreationRequest(
    string BackupId,
    string Trigger,
    DateTimeOffset RequestedAt);

public sealed record BackupCreationResult(
    string BackupId,
    string DestinationRelativePath,
    string ManifestSha256,
    string DatabaseSha256,
    long DatabaseBytes,
    int ObjectCount,
    long ObjectBytes,
    int SecretEnvelopeCount,
    long SecretEnvelopeBytes,
    string? DatabaseMigrationId,
    long DatabaseDataVersion,
    DateTimeOffset CompletedAt,
    BackupVerificationResult Verification);

public sealed record BackupVerificationResult(
    bool Verified,
    string BackupId,
    string ManifestSha256,
    DateTimeOffset CheckedAt,
    string IntegrityResult,
    int VerifiedFileCount,
    long VerifiedBytes,
    string? DatabaseMigrationId,
    string? ErrorCode = null,
    string? SafeErrorDetail = null);

public sealed record BackupRestorePlan(
    bool CanRestore,
    string BackupId,
    DateTimeOffset CheckedAt,
    string IntegrityResult,
    string? BackupMigrationId,
    string? CurrentMigrationId,
    bool RequiresMigration,
    bool ManagedScansIncluded,
    IReadOnlyList<string> RequiredActions,
    string? ErrorCode = null,
    string? SafeErrorDetail = null);

public sealed record BackupConfigurationStatus(
    bool Enabled,
    bool Configured,
    bool EncryptionConfirmed,
    bool DestinationAccessible,
    string? DestinationRootPath,
    bool IncludeManagedScans,
    int ScheduleLocalHour,
    int ScheduleLocalMinute,
    string? ErrorCode);

public sealed record BackupRetentionCandidate(
    string BackupId,
    string DestinationRelativePath,
    string ManifestSha256,
    DateTimeOffset CompletedAt);

public sealed record BackupRetentionFailure(
    string BackupId,
    string ErrorCode,
    string SafeErrorDetail);

public sealed record BackupRetentionResult(
    IReadOnlyList<string> ExpiredBackupIds,
    IReadOnlyList<BackupRetentionFailure> Failures);

public sealed record BackupManifest(
    int FormatVersion,
    string Product,
    string BackupId,
    string Trigger,
    DateTimeOffset RequestedAt,
    DateTimeOffset SnapshotCreatedAt,
    DateTimeOffset CompletedAt,
    string ApplicationVersion,
    string? DatabaseMigrationId,
    long DatabaseDataVersion,
    int DatabaseSchemaVersion,
    long DatabasePageCount,
    bool DestinationEncryptionConfirmed,
    bool ManagedScansIncluded,
    IReadOnlyList<BackupManifestEntry> Files);

public sealed record BackupManifestEntry(
    string Role,
    string RelativePath,
    string Sha256,
    long Bytes,
    string? FileObjectId = null,
    string? StorageClass = null,
    string? RetentionClass = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(BackupManifest))]
internal sealed partial class BackupJsonContext : JsonSerializerContext;

public sealed class BackupOperationException : Exception
{
    public BackupOperationException(
        string errorCode,
        string safeDetail,
        bool isConfigurationError = false,
        Exception? innerException = null)
        : base(safeDetail, innerException)
    {
        ErrorCode = errorCode;
        SafeDetail = safeDetail;
        IsConfigurationError = isConfigurationError;
    }

    public string ErrorCode { get; }

    public string SafeDetail { get; }

    public bool IsConfigurationError { get; }
}

public interface IBackupArchiveService
{
    BackupConfigurationStatus GetConfigurationStatus();

    Task<BackupCreationResult> CreateAsync(
        BackupCreationRequest request,
        CancellationToken cancellationToken = default);

    Task<BackupVerificationResult> VerifyAsync(
        string backupId,
        string destinationRelativePath,
        string? expectedManifestSha256 = null,
        CancellationToken cancellationToken = default);

    Task<BackupRestorePlan> ValidateRestorePlanAsync(
        string backupId,
        string destinationRelativePath,
        string? expectedManifestSha256 = null,
        CancellationToken cancellationToken = default);
}

public interface IBackupRetentionService
{
    Task<BackupRetentionResult> PruneAsync(
        IReadOnlyList<BackupRetentionCandidate> candidates,
        CancellationToken cancellationToken = default);
}
