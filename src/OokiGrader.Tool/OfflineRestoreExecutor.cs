using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OokiGrader.Application.Identifiers;
using OokiGrader.Infrastructure.Backups;

namespace OokiGrader.Tool;

internal sealed record RestoreExecutionRequest(
    string DatabasePath,
    string DataRoot,
    string ContentRoot,
    string DestinationRoot,
    string BackupId,
    string DestinationRelativePath,
    string ExpectedManifestSha256,
    bool MaintenanceConfirmed,
    bool OfflineConfirmed,
    string RestoreConfirmation);

internal sealed record OfflineRestoreResult(
    DateTimeOffset CompletedAt,
    string BackupId,
    string ManifestSha256,
    int RestoredFileCount,
    long RestoredBytes,
    string RollbackSnapshotId,
    bool ManagedScansIncluded);

internal sealed class RestoreExecutionException : Exception
{
    public RestoreExecutionException(
        string errorCode,
        string safeDetail,
        bool mutationPerformed,
        bool safetyRefusal,
        bool preserveRecoveryArtifacts = false,
        Exception? innerException = null)
        : base(safeDetail, innerException)
    {
        ErrorCode = errorCode;
        SafeDetail = safeDetail;
        MutationPerformed = mutationPerformed;
        SafetyRefusal = safetyRefusal;
        PreserveRecoveryArtifacts = preserveRecoveryArtifacts;
    }

    public string ErrorCode { get; }

    public string SafeDetail { get; }

    public bool MutationPerformed { get; }

    public bool SafetyRefusal { get; }

    public bool PreserveRecoveryArtifacts { get; }
}

internal interface IRestoreDirectoryOperations
{
    bool DirectoryExists(string path);

    void MoveDirectory(string source, string destination);
}

internal sealed class SystemRestoreDirectoryOperations :
    IRestoreDirectoryOperations
{
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void MoveDirectory(string source, string destination) =>
        Directory.Move(source, destination);
}

internal static class RestoreDirectorySwitcher
{
    public static void Switch(
        string stagingRoot,
        string liveRoot,
        string rollbackRoot,
        IRestoreDirectoryOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (!operations.DirectoryExists(stagingRoot)
            || !operations.DirectoryExists(liveRoot)
            || operations.DirectoryExists(rollbackRoot))
        {
            throw new RestoreExecutionException(
                "restore_switch_precondition_failed",
                "The offline directory switch preconditions are not satisfied.",
                mutationPerformed: false,
                safetyRefusal: true);
        }

        try
        {
            operations.MoveDirectory(liveRoot, rollbackRoot);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
            throw new RestoreExecutionException(
                "restore_live_root_move_failed",
                "The live data root could not be preserved as a rollback snapshot.",
                mutationPerformed: false,
                safetyRefusal: false,
                innerException: exception);
        }

        try
        {
            operations.MoveDirectory(stagingRoot, liveRoot);
        }
        catch (Exception switchException) when (
            switchException is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
            try
            {
                operations.MoveDirectory(rollbackRoot, liveRoot);
            }
            catch (Exception rollbackException) when (
                rollbackException is IOException
                    or UnauthorizedAccessException
                    or InvalidOperationException)
            {
                throw new RestoreExecutionException(
                    "restore_switch_recovery_required",
                    "The restored root could not be activated and the preserved rollback snapshot requires technician recovery.",
                    mutationPerformed: true,
                    safetyRefusal: false,
                    preserveRecoveryArtifacts: true,
                    innerException: new AggregateException(
                        switchException,
                        rollbackException));
            }

            throw new RestoreExecutionException(
                "restore_switch_failed_rolled_back",
                "The restored root could not be activated; the original live data root was restored.",
                mutationPerformed: true,
                safetyRefusal: false,
                innerException: switchException);
        }
    }
}

internal sealed class OfflineRestoreExecutor
{
    private const int BufferSize = 128 * 1024;
    private const int MaximumManifestEntries = 100_000;
    private const int MaximumManifestBytes = 16 * 1024 * 1024;
    private const long RestoreHeadroomBytes = 64L * 1024 * 1024;
    private static readonly JsonSerializerOptions ManifestJsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly IBackupArchiveService _backupService;
    private readonly TimeProvider _timeProvider;
    private readonly IRestoreDirectoryOperations _directoryOperations;

    public OfflineRestoreExecutor(
        IBackupArchiveService backupService,
        TimeProvider timeProvider,
        IRestoreDirectoryOperations? directoryOperations = null)
    {
        ArgumentNullException.ThrowIfNull(backupService);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _backupService = backupService;
        _timeProvider = timeProvider;
        _directoryOperations =
            directoryOperations ?? new SystemRestoreDirectoryOperations();
    }

    public async Task<OfflineRestoreResult> ExecuteAsync(
        RestoreExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateConfirmations(request);
        ValidatePathLayout(request);

        var health = await new ReadOnlyHealthInspector(_timeProvider)
            .InspectAsync(
                request.DatabasePath,
                request.DataRoot,
                request.ContentRoot,
                cancellationToken)
            .ConfigureAwait(false);
        ValidateCurrentHealth(health);

        var plan = await _backupService
            .ValidateRestorePlanAsync(
                request.BackupId,
                request.DestinationRelativePath,
                request.ExpectedManifestSha256,
                cancellationToken)
            .ConfigureAwait(false);
        if (!plan.CanRestore)
        {
            throw Safety(
                plan.ErrorCode ?? "restore_backup_verification_failed",
                plan.SafeErrorDetail
                    ?? "The selected backup did not pass restore verification.");
        }

        if (plan.RequiresMigration)
        {
            throw Safety(
                "restore_schema_migration_required",
                "The selected backup requires an explicitly supported migration and cannot be executed by this offline tool.");
        }

        var manifest = await ReadVerifiedManifestAsync(
                request,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureRestoreCapacity(request.DataRoot, health, manifest);

        var liveParent = Path.GetDirectoryName(request.DataRoot)
            ?? throw Safety(
                "restore_live_parent_invalid",
                "The live data root parent is unavailable.");
        var liveLeaf = Path.GetFileName(request.DataRoot);
        var stagingRoot = Path.Combine(
            liveParent,
            $".{liveLeaf}.restore-staging-{request.BackupId}");
        var rollbackRoot = Path.Combine(
            liveParent,
            $"{liveLeaf}.rollback-{request.BackupId}");
        SafePaths.EnsureExistingPathChainHasNoReparsePoints(
            liveParent,
            "Live data parent");
        if (Directory.Exists(stagingRoot)
            || File.Exists(stagingRoot)
            || Directory.Exists(rollbackRoot)
            || File.Exists(rollbackRoot))
        {
            throw Safety(
                "restore_recovery_path_exists",
                "A staging or rollback path for this backup already exists; inspect it before retrying.");
        }

        var currentMarker = Path.Combine(
            request.DataRoot,
            "operations",
            "restore.in-progress");
        EnsureSafeCurrentOperationPath(request.DataRoot);
        var stagingMarker = Path.Combine(
            stagingRoot,
            "operations",
            "restore.in-progress");
        var stagingCreated = false;
        var currentMarkerCreated = false;
        try
        {
            Directory.CreateDirectory(stagingRoot);
            stagingCreated = true;
            SafePaths.EnsureExistingPathChainHasNoReparsePoints(
                stagingRoot,
                "Restore staging root");
            Directory.CreateDirectory(Path.Combine(stagingRoot, "objects"));
            Directory.CreateDirectory(Path.Combine(stagingRoot, "secrets"));
            Directory.CreateDirectory(Path.Combine(stagingRoot, "operations"));

            var copyResult = await CopyManifestFilesAsync(
                    request,
                    manifest,
                    stagingRoot,
                    cancellationToken)
                .ConfigureAwait(false);
            var stagedDatabase = Path.Combine(
                stagingRoot,
                "ooki-grader.db");
            await PrepareRestoredDatabaseAsync(
                    stagedDatabase,
                    request.DataRoot,
                    request.BackupId,
                    request.ExpectedManifestSha256,
                    manifest.ManagedScansIncluded,
                    cancellationToken)
                .ConfigureAwait(false);
            await ValidateStagedRootAsync(
                    request,
                    stagingRoot,
                    stagedDatabase,
                    cancellationToken)
                .ConfigureAwait(false);

            var completedAt = _timeProvider.GetUtcNow();
            var markerBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                formatVersion = 1,
                state = "awaiting_administrator_signoff",
                backupId = request.BackupId,
                manifestSha256 = request.ExpectedManifestSha256.ToLowerInvariant(),
                restoredAt = completedAt,
                rollbackSnapshotId = request.BackupId,
            });
            await WriteDurableFileAsync(
                    stagingMarker,
                    markerBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            await WriteDurableFileAsync(
                    currentMarker,
                    markerBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            currentMarkerCreated = true;

            ProbeExclusiveDatabaseAccess(request.DatabasePath);
            RestoreDirectorySwitcher.Switch(
                stagingRoot,
                request.DataRoot,
                rollbackRoot,
                _directoryOperations);
            stagingCreated = false;
            currentMarkerCreated = false;

            return new OfflineRestoreResult(
                completedAt,
                request.BackupId,
                request.ExpectedManifestSha256.ToLowerInvariant(),
                copyResult.FileCount,
                copyResult.Bytes,
                request.BackupId,
                manifest.ManagedScansIncluded);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            CleanupBeforeSwitch(
                stagingRoot,
                request.DataRoot,
                currentMarker,
                stagingCreated,
                currentMarkerCreated);
            throw;
        }
        catch (RestoreExecutionException exception)
        {
            if (!exception.PreserveRecoveryArtifacts)
            {
                CleanupBeforeSwitch(
                    stagingRoot,
                    request.DataRoot,
                    currentMarker,
                    stagingCreated,
                    currentMarkerCreated
                        || exception.ErrorCode
                            == "restore_switch_failed_rolled_back");
            }

            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or InvalidDataException
                or JsonException
                or SqliteException
                or CryptographicException
                or OverflowException
                or ArgumentException)
        {
            CleanupBeforeSwitch(
                stagingRoot,
                request.DataRoot,
                currentMarker,
                stagingCreated,
                currentMarkerCreated);
            throw new RestoreExecutionException(
                "restore_execution_failed",
                "The offline restore failed before activation; the live data root was not replaced.",
                mutationPerformed: currentMarkerCreated,
                safetyRefusal: false,
                innerException: exception);
        }
    }

    private static void ValidateConfirmations(RestoreExecutionRequest request)
    {
        if (!request.MaintenanceConfirmed)
        {
            throw Safety(
                "restore_maintenance_confirmation_required",
                "Offline restore requires explicit maintenance-mode confirmation.");
        }

        if (!request.OfflineConfirmed)
        {
            throw Safety(
                "restore_offline_confirmation_required",
                "Offline restore requires explicit confirmation that the service and all mutation processes are stopped.");
        }

        if (!string.Equals(
                request.RestoreConfirmation,
                request.BackupId,
                StringComparison.Ordinal))
        {
            throw Safety(
                "restore_destructive_confirmation_mismatch",
                "The typed restore confirmation must exactly match the backup identifier.");
        }
    }

    private static void ValidatePathLayout(RestoreExecutionRequest request)
    {
        var expectedDatabase = Path.Combine(
            request.DataRoot,
            "ooki-grader.db");
        var expectedContent = Path.Combine(request.DataRoot, "objects");
        if (!SafePaths.Equal(request.DatabasePath, expectedDatabase)
            || !SafePaths.Equal(request.ContentRoot, expectedContent))
        {
            throw Safety(
                "restore_nonstandard_layout_rejected",
                "Atomic restore requires the database and object store to use the standard data-root layout.");
        }

        if (SafePaths.IsSameOrNested(
                request.DestinationRoot,
                request.DataRoot)
            || SafePaths.IsSameOrNested(
                request.DataRoot,
                request.DestinationRoot))
        {
            throw Safety(
                "restore_backup_root_overlap",
                "The encrypted backup destination and live data root may not overlap.");
        }
    }

    private static void ValidateCurrentHealth(HealthCommandResult health)
    {
        if (health.Storage.RestoreOrMigrationMarkerPresent)
        {
            throw Safety(
                "restore_operation_marker_present",
                "An existing restore or migration marker must be resolved before restoring.");
        }

        if (health.Database.State != "healthy"
            || health.Database.IntegrityResult != "ok"
            || !health.Database.SchemaCurrent
            || !health.Database.ConfiguredDataRootMatches)
        {
            throw Safety(
                "restore_live_database_unhealthy",
                "The current live database must pass integrity, schema, and data-root checks before rollback preservation.");
        }

        if (!health.Database.MaintenanceMode)
        {
            throw Safety(
                "restore_maintenance_mode_required",
                "The current database is not in maintenance mode.");
        }

        if (!health.Storage.DataRootReadable
            || !health.Storage.ContentRootReadable
            || !health.Storage.ReserveSatisfied)
        {
            throw Safety(
                "restore_live_storage_unhealthy",
                "The current live storage is not safe for an offline restore.");
        }
    }

    private static void EnsureSafeCurrentOperationPath(string dataRoot)
    {
        var operations = Path.Combine(dataRoot, "operations");
        if (File.Exists(operations))
        {
            throw Safety(
                "restore_operation_path_invalid",
                "The live operation-marker path is not a safe directory.");
        }

        try
        {
            SafePaths.EnsureExistingPathChainHasNoReparsePoints(
                operations,
                "Live operation-marker path");
        }
        catch (ToolUsageException exception)
        {
            throw new RestoreExecutionException(
                "restore_operation_path_unsafe",
                "The live operation-marker path traverses an unsafe filesystem link.",
                mutationPerformed: false,
                safetyRefusal: true,
                innerException: exception);
        }
    }

    private static void EnsureRestoreCapacity(
        string dataRoot,
        HealthCommandResult health,
        BackupManifest manifest)
    {
        var required = checked(
            manifest.Files.Sum(file => file.Bytes)
            + (health.Database.PhysicalFreeReserveBytes ?? 0)
            + RestoreHeadroomBytes);
        try
        {
            var root = Path.GetPathRoot(dataRoot)
                ?? throw new IOException();
            if (new DriveInfo(root).AvailableFreeSpace < required)
            {
                throw Safety(
                    "restore_insufficient_capacity",
                    "The data volume does not have enough free space to stage and verify the restore while preserving its reserve.");
            }
        }
        catch (RestoreExecutionException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException)
        {
            throw new RestoreExecutionException(
                "restore_capacity_unavailable",
                "The available capacity for restore staging could not be verified.",
                mutationPerformed: false,
                safetyRefusal: true,
                innerException: exception);
        }
    }

    private static async Task<BackupManifest> ReadVerifiedManifestAsync(
        RestoreExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var backupPath = SafePaths.ResolveUnderRoot(
            request.DestinationRoot,
            request.DestinationRelativePath,
            "Backup set",
            requireExisting: true);
        var manifestPath = SafePaths.ResolveUnderRoot(
            backupPath,
            "manifest.json",
            "Backup manifest",
            requireExisting: true);
        var info = new FileInfo(manifestPath);
        if (info.Length is <= 0 or > MaximumManifestBytes)
        {
            throw Safety(
                "restore_manifest_size_invalid",
                "The verified backup manifest size is invalid.");
        }

        var bytes = await File.ReadAllBytesAsync(
                manifestPath,
                cancellationToken)
            .ConfigureAwait(false);
        var actualHash = Convert.ToHexString(SHA256.HashData(bytes))
            .ToLowerInvariant();
        if (!FixedTimeHexEquals(
                actualHash,
                request.ExpectedManifestSha256))
        {
            throw Safety(
                "restore_manifest_hash_mismatch",
                "The backup manifest changed after verification.");
        }

        var manifest = JsonSerializer.Deserialize<BackupManifest>(
            bytes,
            ManifestJsonOptions);
        if (manifest is null
            || manifest.FormatVersion != 1
            || manifest.Product != "OokiGrader"
            || manifest.BackupId != request.BackupId
            || manifest.Files is null
            || manifest.Files.Count is <= 0 or > MaximumManifestEntries)
        {
            throw Safety(
                "restore_manifest_invalid",
                "The verified backup manifest cannot be restored.");
        }

        return manifest;
    }

    private static async Task<CopySummary> CopyManifestFilesAsync(
        RestoreExecutionRequest request,
        BackupManifest manifest,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        var backupPath = SafePaths.ResolveUnderRoot(
            request.DestinationRoot,
            request.DestinationRelativePath,
            "Backup set",
            requireExisting: true);
        var targetPaths = new HashSet<string>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        var databaseCount = 0;
        long copiedBytes = 0;
        var copiedFiles = 0;
        foreach (var entry in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateManifestEntry(entry);
            var source = SafePaths.ResolveUnderRoot(
                backupPath,
                entry.RelativePath,
                "Backup file",
                requireExisting: true);
            if (!File.Exists(source))
            {
                throw Safety(
                    "restore_backup_file_missing",
                    "A verified backup file is unavailable.");
            }

            var targetRelativePath = MapRestoreTarget(entry);
            if (entry.Role == "database")
            {
                databaseCount++;
            }

            var target = SafePaths.ResolveUnderRoot(
                stagingRoot,
                targetRelativePath,
                "Restore target");
            if (!targetPaths.Add(target))
            {
                throw Safety(
                    "restore_target_duplicate",
                    "The backup maps more than one file to the same restore target.");
            }

            var hash = await CopyAndHashAsync(
                    source,
                    target,
                    entry.Bytes,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!FixedTimeHexEquals(hash, entry.Sha256))
            {
                throw Safety(
                    "restore_backup_file_hash_mismatch",
                    "A backup file changed while the restore was being staged.");
            }

            copiedBytes = checked(copiedBytes + entry.Bytes);
            copiedFiles++;
        }

        if (databaseCount != 1)
        {
            throw Safety(
                "restore_database_entry_invalid",
                "The backup must contain exactly one database entry.");
        }

        return new CopySummary(copiedFiles, copiedBytes);
    }

    private static void ValidateManifestEntry(BackupManifestEntry entry)
    {
        if (entry.Bytes < 0
            || !IsSha256(entry.Sha256)
            || string.IsNullOrWhiteSpace(entry.Role)
            || entry.RelativePath.Contains('\\', StringComparison.Ordinal)
            || !SafePaths.IsSafeRelativePath(entry.RelativePath))
        {
            throw Safety(
                "restore_manifest_entry_invalid",
                "A backup manifest entry is invalid.");
        }
    }

    private static string MapRestoreTarget(BackupManifestEntry entry)
    {
        return entry.Role switch
        {
            "database" when entry.RelativePath == "database/ooki-grader.db"
                => "ooki-grader.db",
            "content_object" when entry.RelativePath.StartsWith(
                "objects/",
                StringComparison.Ordinal)
                => entry.RelativePath,
            "protected_secret_envelope" when entry.RelativePath.StartsWith(
                "secrets/",
                StringComparison.Ordinal)
                && entry.RelativePath.EndsWith(
                    ".secret",
                    StringComparison.OrdinalIgnoreCase)
                => entry.RelativePath,
            _ => throw Safety(
                "restore_manifest_role_invalid",
                "A backup manifest entry has an unsupported restore role or path."),
        };
    }

    private async Task PrepareRestoredDatabaseAsync(
        string databasePath,
        string liveDataRoot,
        string backupId,
        string manifestSha256,
        bool managedScansIncluded,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE site_settings
                SET maintenance_mode = 1,
                    data_root = $data_root,
                    updated_at = $updated_at,
                    revision = revision + 1
                WHERE id = 'site';
                """;
            update.Parameters.AddWithValue("$data_root", liveDataRoot);
            update.Parameters.AddWithValue(
                "$updated_at",
                now.ToUnixTimeMilliseconds());
            if (await update.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false)
                != 1)
            {
                throw new InvalidDataException(
                    "The restored site settings are unavailable.");
            }
        }

        await using (var audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText =
                """
                INSERT INTO audit_event (
                    id,
                    occurred_at,
                    actor_staff_user_id,
                    event_type,
                    object_type,
                    object_id,
                    outcome,
                    reason_code,
                    correlation_id,
                    source_ip_prefix,
                    safe_metadata_json)
                VALUES (
                    $id,
                    $occurred_at,
                    NULL,
                    'restore.executed',
                    'backup',
                    $backup_id,
                    'success',
                    'offline_restore',
                    $correlation_id,
                    NULL,
                    $metadata);
                """;
            audit.Parameters.AddWithValue("$id", UlidId.New(now));
            audit.Parameters.AddWithValue(
                "$occurred_at",
                now.ToUnixTimeMilliseconds());
            audit.Parameters.AddWithValue("$backup_id", backupId);
            audit.Parameters.AddWithValue(
                "$correlation_id",
                $"offline-restore:{backupId}");
            audit.Parameters.AddWithValue(
                "$metadata",
                JsonSerializer.Serialize(new
                {
                    backupId,
                    manifestSha256 = manifestSha256.ToLowerInvariant(),
                    managedScansIncluded,
                    maintenanceModeEnforced = true,
                }));
            await audit.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ValidateStagedRootAsync(
        RestoreExecutionRequest request,
        string stagingRoot,
        string stagedDatabase,
        CancellationToken cancellationToken)
    {
        var stagedHealth = await new ReadOnlyHealthInspector(_timeProvider)
            .InspectAsync(
                stagedDatabase,
                request.DataRoot,
                Path.Combine(stagingRoot, "objects"),
                cancellationToken)
            .ConfigureAwait(false);
        if (stagedHealth.Database.State != "healthy"
            || stagedHealth.Database.IntegrityResult != "ok"
            || !stagedHealth.Database.SchemaCurrent
            || !stagedHealth.Database.ConfiguredDataRootMatches
            || !stagedHealth.Database.MaintenanceMode)
        {
            throw new RestoreExecutionException(
                "restore_staging_validation_failed",
                "The staged restore failed database integrity, schema, data-root, or maintenance validation.",
                mutationPerformed: false,
                safetyRefusal: false);
        }
    }

    private static async Task<string> CopyAndHashAsync(
        string sourcePath,
        string destinationPath,
        long expectedBytes,
        CancellationToken cancellationToken)
    {
        SafePaths.EnsureExistingPathChainHasNoReparsePoints(
            sourcePath,
            "Backup file");
        var sourceInfo = new FileInfo(sourcePath);
        if (sourceInfo.Length != expectedBytes
            || (sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "A backup source file is not safe to copy.");
        }

        Directory.CreateDirectory(
            Path.GetDirectoryName(destinationPath)!);
        SafePaths.EnsureExistingPathChainHasNoReparsePoints(
            Path.GetDirectoryName(destinationPath)!,
            "Restore target");
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous
                | FileOptions.SequentialScan
                | FileOptions.WriteThrough);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long copied = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(
                    buffer.AsMemory(0, buffer.Length),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                copied = checked(copied + read);
                if (copied > expectedBytes)
                {
                    throw new InvalidDataException(
                        "A backup source file changed while it was copied.");
                }

                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken).ConfigureAwait(false);
            }

            if (copied != expectedBytes)
            {
                throw new InvalidDataException(
                    "A backup source file changed while it was copied.");
            }

            await destination.FlushAsync(cancellationToken)
                .ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
            return Convert.ToHexString(hash.GetHashAndReset())
                .ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static async Task WriteDurableFileAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken)
            .ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken)
            .ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static void ProbeExclusiveDatabaseAccess(string databasePath)
    {
        try
        {
            using var stream = new FileStream(
                databasePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.RandomAccess);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new RestoreExecutionException(
                "restore_database_not_offline",
                "The live database is still open or cannot be locked exclusively; the service must remain stopped.",
                mutationPerformed: false,
                safetyRefusal: true,
                innerException: exception);
        }
    }

    private static void CleanupBeforeSwitch(
        string stagingRoot,
        string dataRoot,
        string currentMarker,
        bool stagingCreated,
        bool removeCurrentMarker)
    {
        if (removeCurrentMarker
            && File.Exists(currentMarker)
            && SafePaths.IsSameOrNested(currentMarker, dataRoot))
        {
            try
            {
                if (!SafePaths.IsReparsePoint(currentMarker))
                {
                    File.Delete(currentMarker);
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        if (stagingCreated && Directory.Exists(stagingRoot))
        {
            TryDeleteOwnedStaging(stagingRoot);
        }
    }

    private static void TryDeleteOwnedStaging(string stagingRoot)
    {
        try
        {
            var leaf = Path.GetFileName(stagingRoot);
            if (!leaf.StartsWith('.')
                || !leaf.Contains(
                    ".restore-staging-",
                    StringComparison.Ordinal))
            {
                return;
            }

            var pending = new Stack<string>();
            pending.Push(stagingRoot);
            var count = 0;
            while (pending.TryPop(out var directory))
            {
                if (SafePaths.IsReparsePoint(directory))
                {
                    return;
                }

                foreach (var entry in Directory.EnumerateFileSystemEntries(
                    directory,
                    "*",
                    SearchOption.TopDirectoryOnly))
                {
                    count++;
                    if (count > MaximumManifestEntries * 2 + 64
                        || SafePaths.IsReparsePoint(entry))
                    {
                        return;
                    }

                    if (Directory.Exists(entry))
                    {
                        pending.Push(entry);
                    }
                }
            }

            Directory.Delete(stagingRoot, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
        }
    }

    private static RestoreExecutionException Safety(
        string errorCode,
        string safeDetail) =>
        new(
            errorCode,
            safeDetail,
            mutationPerformed: false,
            safetyRefusal: true);

    private static bool IsSha256(string value) =>
        value.Length == 64
        && value.All(character =>
            character is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F');

    private static bool FixedTimeHexEquals(string left, string right)
    {
        if (!IsSha256(left) || !IsSha256(right))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left),
            Convert.FromHexString(right));
    }

    private sealed record CopySummary(int FileCount, long Bytes);
}
