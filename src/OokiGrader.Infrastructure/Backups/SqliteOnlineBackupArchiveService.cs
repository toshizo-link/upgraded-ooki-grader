using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;

namespace OokiGrader.Infrastructure.Backups;

public sealed class SqliteOnlineBackupArchiveService :
    IBackupArchiveService,
    IBackupRetentionService
{
    private const string DatabaseRelativePath = "database/ooki-grader.db";
    private const string ManifestFileName = "manifest.json";
    private const string ManifestHashFileName = "manifest.sha256";
    private const int BufferSize = 128 * 1024;
    private static readonly TimeSpan MaximumScanBackupLifetime =
        TimeSpan.FromDays(7);
    private static readonly HashSet<ContentStorageClass> DefaultStorageClasses =
    [
        ContentStorageClass.TemplateSource,
        ContentStorageClass.TemplateDerived,
        ContentStorageClass.ResultReport,
    ];

    private readonly BackupOptions _options;
    private readonly IContentStore _contentStore;
    private readonly TimeProvider _timeProvider;
    private readonly string _databasePath;
    private readonly string _contentRootPath;
    private readonly string? _secretEnvelopeRootPath;
    private readonly string? _destinationRootPath;
    private readonly StringComparison _pathComparison;

    public SqliteOnlineBackupArchiveService(
        BackupOptions options,
        IContentStore contentStore,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(contentStore);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ValidateLimits(options);

        _options = options;
        _contentStore = contentStore;
        _timeProvider = timeProvider;
        _databasePath = NormalizeRequiredPath(
            options.DatabasePath,
            nameof(options.DatabasePath));
        _contentRootPath = NormalizeRequiredPath(
            options.ContentRootPath,
            nameof(options.ContentRootPath));
        _secretEnvelopeRootPath = NormalizeOptionalPath(
            options.SecretEnvelopeRootPath,
            nameof(options.SecretEnvelopeRootPath));
        _destinationRootPath = NormalizeOptionalPath(
            options.DestinationRootPath,
            nameof(options.DestinationRootPath));
        _pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (_destinationRootPath is not null)
        {
            RejectFilesystemRoot(_destinationRootPath, nameof(options.DestinationRootPath));
            var databaseRoot = Path.GetDirectoryName(_databasePath)!;
            if (IsSameOrNestedPath(_destinationRootPath, _contentRootPath)
                || IsSameOrNestedPath(_destinationRootPath, databaseRoot))
            {
                throw new ArgumentException(
                    "The backup destination may not be inside the live data root.",
                    nameof(options));
            }
        }
    }

    public BackupConfigurationStatus GetConfigurationStatus()
    {
        if (_destinationRootPath is null)
        {
            return Status(
                configured: false,
                accessible: false,
                errorCode: _options.Enabled
                    ? "backup_destination_not_configured"
                    : "backup_disabled");
        }

        if (!_options.DestinationEncryptionConfirmed)
        {
            return Status(
                configured: true,
                accessible: Directory.Exists(_destinationRootPath),
                errorCode: _options.Enabled
                    ? "backup_destination_encryption_unconfirmed"
                    : "backup_disabled");
        }

        try
        {
            if (!Directory.Exists(_destinationRootPath))
            {
                if (!_options.ProbeDestinationWriteAccess)
                {
                    return Status(
                        configured: true,
                        accessible: false,
                        errorCode: "backup_destination_unavailable");
                }

                Directory.CreateDirectory(_destinationRootPath);
            }

            EnsureNotReparsePoint(_destinationRootPath);
            if (!_options.ProbeDestinationWriteAccess)
            {
                using var enumerator = Directory
                    .EnumerateFileSystemEntries(
                        _destinationRootPath,
                        "*",
                        SearchOption.TopDirectoryOnly)
                    .GetEnumerator();
                _ = enumerator.MoveNext();
                return Status(
                    configured: true,
                    accessible: true,
                    errorCode: _options.Enabled ? null : "backup_disabled");
            }

            var probePath = Path.Combine(
                _destinationRootPath,
                $".write-probe-{Guid.NewGuid():N}");
            using (File.Create(probePath, 1, FileOptions.DeleteOnClose))
            {
            }

            return Status(
                configured: true,
                accessible: true,
                errorCode: _options.Enabled ? null : "backup_disabled");
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            return Status(
                configured: true,
                accessible: false,
                errorCode: "backup_destination_unavailable");
        }
    }

    public async Task<BackupCreationResult> CreateAsync(
        BackupCreationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateBackupId(request.BackupId);
        ValidateTrigger(request.Trigger);
        var destinationRoot = RequireUsableDestination(requireEnabled: true);

        if (!File.Exists(_databasePath))
        {
            throw new BackupOperationException(
                "backup_database_missing",
                "The live database file is unavailable.");
        }

        var finalRelativePath = BuildFinalRelativePath(
            request.BackupId,
            request.RequestedAt);
        var finalPath = ResolveUnderRoot(destinationRoot, finalRelativePath);
        if (Directory.Exists(finalPath))
        {
            var existingVerification = await VerifyAsync(
                    request.BackupId,
                    finalRelativePath,
                    expectedManifestSha256: null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!existingVerification.Verified)
            {
                throw new BackupOperationException(
                    existingVerification.ErrorCode ?? "backup_existing_set_invalid",
                    existingVerification.SafeErrorDetail
                    ?? "An existing backup set with this identifier is invalid.");
            }

            return await ReadExistingCreationResultAsync(
                    finalPath,
                    finalRelativePath,
                    existingVerification,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var stagingRoot = ResolveUnderRoot(
            destinationRoot,
            Path.Combine(".staging", request.BackupId));
        PrepareEmptyStagingDirectory(stagingRoot, destinationRoot);

        try
        {
            var databasePath = ResolveUnderRoot(stagingRoot, DatabaseRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            await CreateOnlineSnapshotAsync(databasePath, cancellationToken)
                .ConfigureAwait(false);

            var snapshot = await ReadSnapshotMetadataAsync(
                    databasePath,
                    cancellationToken)
                .ConfigureAwait(false);
            var files = new List<BackupManifestEntry>(
                Math.Min(
                    checked(snapshot.ContentObjects.Count + 8),
                    _options.MaximumManifestEntries));
            var databaseHash = await HashFileAsync(databasePath, cancellationToken)
                .ConfigureAwait(false);
            files.Add(new BackupManifestEntry(
                "database",
                NormalizeManifestPath(DatabaseRelativePath),
                databaseHash,
                new FileInfo(databasePath).Length));

            long objectBytes = 0;
            foreach (var contentObject in snapshot.ContentObjects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureManifestCapacity(files.Count + 1);
                var entry = await CopyContentObjectAsync(
                        contentObject,
                        stagingRoot,
                        cancellationToken)
                    .ConfigureAwait(false);
                files.Add(entry);
                objectBytes = checked(objectBytes + entry.Bytes);
            }

            long secretBytes = 0;
            var secretCount = 0;
            if (_secretEnvelopeRootPath is not null
                && Directory.Exists(_secretEnvelopeRootPath))
            {
                foreach (var sourcePath in EnumerateSecretEnvelopes(
                    _secretEnvelopeRootPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureManifestCapacity(files.Count + 1);
                    var entry = await CopySecretEnvelopeAsync(
                            sourcePath,
                            stagingRoot,
                            cancellationToken)
                        .ConfigureAwait(false);
                    files.Add(entry);
                    secretCount++;
                    secretBytes = checked(secretBytes + entry.Bytes);
                }
            }

            var completedAt = _timeProvider.GetUtcNow();
            var manifest = new BackupManifest(
                FormatVersion: 1,
                Product: "OokiGrader",
                BackupId: request.BackupId,
                Trigger: request.Trigger,
                RequestedAt: request.RequestedAt,
                SnapshotCreatedAt: snapshot.SnapshotCreatedAt,
                CompletedAt: completedAt,
                ApplicationVersion: _options.ApplicationVersion,
                DatabaseMigrationId: snapshot.DatabaseMigrationId,
                DatabaseDataVersion: snapshot.DatabaseDataVersion,
                DatabaseSchemaVersion: snapshot.DatabaseSchemaVersion,
                DatabasePageCount: snapshot.DatabasePageCount,
                DestinationEncryptionConfirmed:
                    _options.DestinationEncryptionConfirmed,
                ManagedScansIncluded: _options.IncludeManagedScans,
                Files: files);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
                manifest,
                BackupJsonContext.Default.BackupManifest);
            if (manifestBytes.Length > _options.MaximumManifestBytes)
            {
                throw new BackupOperationException(
                    "backup_manifest_too_large",
                    "The backup manifest exceeded its configured safety bound.");
            }

            var manifestPath = ResolveUnderRoot(stagingRoot, ManifestFileName);
            await WriteDurableFileAsync(
                    manifestPath,
                    manifestBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            var manifestSha256 = Sha256(manifestBytes);
            await WriteDurableFileAsync(
                    ResolveUnderRoot(stagingRoot, ManifestHashFileName),
                    Encoding.ASCII.GetBytes(
                        $"{manifestSha256}  {ManifestFileName}{Environment.NewLine}"),
                    cancellationToken)
                .ConfigureAwait(false);

            var verification = await VerifyDirectoryAsync(
                    request.BackupId,
                    stagingRoot,
                    manifestSha256,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!verification.Verified)
            {
                throw new BackupOperationException(
                    verification.ErrorCode ?? "backup_verification_failed",
                    verification.SafeErrorDetail
                    ?? "The staged backup did not pass verification.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            Directory.Move(stagingRoot, finalPath);

            return new BackupCreationResult(
                request.BackupId,
                NormalizeManifestPath(finalRelativePath),
                manifestSha256,
                databaseHash,
                new FileInfo(
                    ResolveUnderRoot(finalPath, DatabaseRelativePath)).Length,
                snapshot.ContentObjects.Count,
                objectBytes,
                secretCount,
                secretBytes,
                snapshot.DatabaseMigrationId,
                snapshot.DatabaseDataVersion,
                completedAt,
                verification);
        }
        catch
        {
            TryDeleteStagingDirectory(stagingRoot, destinationRoot);
            throw;
        }
    }

    public async Task<BackupVerificationResult> VerifyAsync(
        string backupId,
        string destinationRelativePath,
        string? expectedManifestSha256 = null,
        CancellationToken cancellationToken = default)
    {
        ValidateBackupId(backupId);
        var destinationRoot = RequireUsableDestination(requireEnabled: false);
        string backupPath;
        try
        {
            backupPath = ResolveUnderRoot(destinationRoot, destinationRelativePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            return VerificationFailure(
                backupId,
                "backup_path_invalid",
                "The backup location is outside the configured destination.");
        }

        if (!Directory.Exists(backupPath))
        {
            return VerificationFailure(
                backupId,
                "backup_set_missing",
                "The backup set directory is unavailable.");
        }

        return await VerifyDirectoryAsync(
                backupId,
                backupPath,
                expectedManifestSha256,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<BackupRestorePlan> ValidateRestorePlanAsync(
        string backupId,
        string destinationRelativePath,
        string? expectedManifestSha256 = null,
        CancellationToken cancellationToken = default)
    {
        var verification = await VerifyAsync(
                backupId,
                destinationRelativePath,
                expectedManifestSha256,
                cancellationToken)
            .ConfigureAwait(false);
        if (!verification.Verified)
        {
            return new BackupRestorePlan(
                CanRestore: false,
                backupId,
                verification.CheckedAt,
                verification.IntegrityResult,
                verification.DatabaseMigrationId,
                CurrentMigrationId: null,
                RequiresMigration: false,
                ManagedScansIncluded: false,
                RequiredActions: Array.Empty<string>(),
                verification.ErrorCode,
                verification.SafeErrorDetail);
        }

        var destinationRoot = RequireUsableDestination(requireEnabled: false);
        var backupPath = ResolveUnderRoot(destinationRoot, destinationRelativePath);
        var manifest = await ReadManifestAsync(backupPath, cancellationToken)
            .ConfigureAwait(false);
        var currentMigrationId = File.Exists(_databasePath)
            ? await ReadLatestMigrationIdAsync(
                    _databasePath,
                    cancellationToken)
                .ConfigureAwait(false)
            : null;
        var requiresMigration = !string.Equals(
            manifest.DatabaseMigrationId,
            currentMigrationId,
            StringComparison.Ordinal);
        var requiredActions = new List<string>
        {
            "Enter maintenance mode and stop all mutation workers.",
            "Preserve the current data root as a recoverable rollback snapshot.",
            "Restore into a new directory and repeat integrity and manifest checks.",
            "Re-enter or rewrap provider credentials for this Windows host.",
            "Start read-only verification and require administrator sign-off.",
        };
        if (requiresMigration)
        {
            requiredActions.Insert(
                3,
                "Use a release that explicitly supports the backup schema before migration.");
        }

        return new BackupRestorePlan(
            CanRestore: true,
            backupId,
            verification.CheckedAt,
            verification.IntegrityResult,
            manifest.DatabaseMigrationId,
            currentMigrationId,
            requiresMigration,
            manifest.ManagedScansIncluded,
            requiredActions);
    }

    public async Task<BackupRetentionResult> PruneAsync(
        IReadOnlyList<BackupRetentionCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count > _options.MaximumRetentionCandidates)
        {
            throw new BackupOperationException(
                "backup_retention_candidate_limit",
                "Backup retention exceeded its configured candidate bound.");
        }

        if (candidates.Count == 0)
        {
            return new BackupRetentionResult(
                Array.Empty<string>(),
                Array.Empty<BackupRetentionFailure>());
        }

        var destinationRoot = RequireRetentionDestination();
        var ordered = candidates
            .OrderByDescending(candidate => candidate.CompletedAt)
            .ThenByDescending(candidate => candidate.BackupId, StringComparer.Ordinal)
            .ToArray();
        var readable = new List<RetentionMetadata>(ordered.Length);
        var failures = new List<BackupRetentionFailure>();

        foreach (var candidate in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ValidateRetentionCandidate(candidate);
                var backupPath = ResolveValidatedBackupSetPath(
                    destinationRoot,
                    candidate);
                EnsurePathChainHasNoReparsePoints(
                    destinationRoot,
                    backupPath,
                    requireLeaf: false);
                if (!Directory.Exists(backupPath))
                {
                    readable.Add(new RetentionMetadata(
                        candidate,
                        backupPath,
                        ManagedScansIncluded: false,
                        Missing: true));
                    continue;
                }

                EnsurePathChainHasNoReparsePoints(
                    destinationRoot,
                    backupPath,
                    requireLeaf: true);
                var manifest = await ReadRetentionManifestAsync(
                        backupPath,
                        candidate,
                        cancellationToken)
                    .ConfigureAwait(false);
                readable.Add(new RetentionMetadata(
                    candidate,
                    backupPath,
                    manifest.ManagedScansIncluded,
                    Missing: false));
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException
                    or JsonException
                    or InvalidOperationException
                    or ArgumentException
                    or CryptographicException)
            {
                failures.Add(RetentionFailure(
                    candidate.BackupId,
                    "backup_retention_path_invalid",
                    "The backup set could not be safely inspected for retention."));
            }
        }

        var newestExisting = readable
            .Where(item => !item.Missing)
            .OrderByDescending(item => item.Candidate.CompletedAt)
            .ThenByDescending(
                item => item.Candidate.BackupId,
                StringComparer.Ordinal)
            .FirstOrDefault();
        if (newestExisting is null)
        {
            return new BackupRetentionResult(
                readable
                    .Where(item => item.Missing)
                    .OrderBy(item => item.Candidate.CompletedAt)
                    .ThenBy(
                        item => item.Candidate.BackupId,
                        StringComparer.Ordinal)
                    .Take(_options.MaximumRetentionDeletesPerRun)
                    .Select(item => item.Candidate.BackupId)
                    .ToArray(),
                failures);
        }

        var newestBackupId = newestExisting.Candidate.BackupId;
        var now = _timeProvider.GetUtcNow();
        var selected = SelectExpiredBackups(
                readable,
                newestBackupId,
                now,
                _options.DailyRetentionDays,
                _options.WeeklyRetentionWeeks,
                _options.MonthlyRetentionMonths)
            .OrderBy(item => item.Candidate.CompletedAt)
            .ThenBy(item => item.Candidate.BackupId, StringComparer.Ordinal)
            .Take(_options.MaximumRetentionDeletesPerRun)
            .ToArray();
        var expired = new List<string>(selected.Length);

        foreach (var item in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Missing)
            {
                expired.Add(item.Candidate.BackupId);
                continue;
            }

            var verification = await VerifyDirectoryAsync(
                    item.Candidate.BackupId,
                    item.BackupPath,
                    item.Candidate.ManifestSha256,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!verification.Verified)
            {
                failures.Add(RetentionFailure(
                    item.Candidate.BackupId,
                    verification.ErrorCode ?? "backup_retention_verification_failed",
                    verification.SafeErrorDetail
                    ?? "The backup set failed verification and was retained."));
                continue;
            }

            try
            {
                DeleteBackupSet(
                    destinationRoot,
                    item.BackupPath,
                    item.Candidate.BackupId);
                expired.Add(item.Candidate.BackupId);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or InvalidOperationException)
            {
                failures.Add(RetentionFailure(
                    item.Candidate.BackupId,
                    "backup_retention_delete_failed",
                    "The expired backup set could not be safely deleted."));
            }
        }

        return new BackupRetentionResult(expired, failures);
    }

    private async Task<BackupVerificationResult> VerifyDirectoryAsync(
        string backupId,
        string backupPath,
        string? expectedManifestSha256,
        CancellationToken cancellationToken)
    {
        try
        {
            EnsurePathChainHasNoReparsePoints(
                _destinationRootPath!,
                backupPath,
                requireLeaf: true);
            var manifestPath = ResolveUnderRoot(backupPath, ManifestFileName);
            var sidecarPath = ResolveUnderRoot(backupPath, ManifestHashFileName);
            if (!File.Exists(manifestPath) || !File.Exists(sidecarPath))
            {
                return VerificationFailure(
                    backupId,
                    "backup_manifest_missing",
                    "The backup manifest or its SHA-256 sidecar is missing.");
            }

            if (new FileInfo(manifestPath).Length > _options.MaximumManifestBytes)
            {
                return VerificationFailure(
                    backupId,
                    "backup_manifest_too_large",
                    "The backup manifest exceeded its configured safety bound.");
            }

            var manifestBytes = await File.ReadAllBytesAsync(
                    manifestPath,
                    cancellationToken)
                .ConfigureAwait(false);
            var actualManifestSha256 = Sha256(manifestBytes);
            var sidecarSha256 = await ReadSidecarHashAsync(
                    sidecarPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!FixedTimeHexEquals(actualManifestSha256, sidecarSha256)
                || (expectedManifestSha256 is not null
                    && !FixedTimeHexEquals(
                        actualManifestSha256,
                        expectedManifestSha256)))
            {
                return VerificationFailure(
                    backupId,
                    "backup_manifest_hash_mismatch",
                    "The backup manifest SHA-256 did not match.",
                    actualManifestSha256);
            }

            var manifest = JsonSerializer.Deserialize(
                manifestBytes,
                BackupJsonContext.Default.BackupManifest);
            if (manifest is null
                || manifest.FormatVersion != 1
                || manifest.Product != "OokiGrader"
                || !string.Equals(
                    manifest.BackupId,
                    backupId,
                    StringComparison.Ordinal)
                || manifest.Files.Count == 0
                || manifest.Files.Count > _options.MaximumManifestEntries)
            {
                return VerificationFailure(
                    backupId,
                    "backup_manifest_invalid",
                    "The backup manifest format or identifier is invalid.",
                    actualManifestSha256);
            }

            var uniquePaths = new HashSet<string>(
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal);
            long verifiedBytes = 0;
            BackupManifestEntry? databaseEntry = null;
            foreach (var entry in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsSha256(entry.Sha256)
                    || entry.Bytes < 0
                    || string.IsNullOrWhiteSpace(entry.Role)
                    || !uniquePaths.Add(entry.RelativePath))
                {
                    return VerificationFailure(
                        backupId,
                        "backup_manifest_entry_invalid",
                        "A backup manifest file entry is invalid.",
                        actualManifestSha256);
                }

                var filePath = ResolveUnderRoot(backupPath, entry.RelativePath);
                if (!File.Exists(filePath))
                {
                    return VerificationFailure(
                        backupId,
                        "backup_file_missing",
                        "A file declared by the backup manifest is missing.",
                        actualManifestSha256);
                }

                EnsurePathChainHasNoReparsePoints(
                    backupPath,
                    filePath,
                    requireLeaf: true);
                EnsureNotReparsePoint(filePath);
                var info = new FileInfo(filePath);
                if (info.Length != entry.Bytes)
                {
                    return VerificationFailure(
                        backupId,
                        "backup_file_length_mismatch",
                        "A backup file length did not match its manifest.",
                        actualManifestSha256);
                }

                var actualSha256 = await HashFileAsync(filePath, cancellationToken)
                    .ConfigureAwait(false);
                if (!FixedTimeHexEquals(entry.Sha256, actualSha256))
                {
                    return VerificationFailure(
                        backupId,
                        "backup_file_hash_mismatch",
                        "A backup file SHA-256 did not match its manifest.",
                        actualManifestSha256);
                }

                verifiedBytes = checked(verifiedBytes + info.Length);
                if (entry.Role == "database")
                {
                    if (databaseEntry is not null)
                    {
                        return VerificationFailure(
                            backupId,
                            "backup_database_entry_invalid",
                            "The backup manifest declares more than one database.",
                            actualManifestSha256);
                    }

                    databaseEntry = entry;
                }
            }

            if (databaseEntry is null
                || !string.Equals(
                    databaseEntry.RelativePath,
                    NormalizeManifestPath(DatabaseRelativePath),
                    StringComparison.Ordinal))
            {
                return VerificationFailure(
                    backupId,
                    "backup_database_entry_missing",
                    "The backup manifest does not declare its SQLite database.",
                    actualManifestSha256);
            }

            var databasePath = ResolveUnderRoot(
                backupPath,
                databaseEntry.RelativePath);
            var integrityResult = await CheckDatabaseIntegrityAsync(
                    databasePath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(integrityResult, "ok", StringComparison.Ordinal))
            {
                return VerificationFailure(
                    backupId,
                    "backup_database_integrity_failed",
                    "The backup database failed SQLite integrity checks.",
                    actualManifestSha256,
                    integrityResult);
            }

            var migrationId = await ReadLatestMigrationIdAsync(
                    databasePath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                migrationId,
                manifest.DatabaseMigrationId,
                StringComparison.Ordinal))
            {
                return VerificationFailure(
                    backupId,
                    "backup_database_schema_mismatch",
                    "The database migration identifier differs from the manifest.",
                    actualManifestSha256);
            }

            return new BackupVerificationResult(
                Verified: true,
                backupId,
                actualManifestSha256,
                _timeProvider.GetUtcNow(),
                integrityResult,
                manifest.Files.Count,
                verifiedBytes,
                migrationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or JsonException
                or SqliteException
                or OverflowException
                or InvalidOperationException
                or ArgumentException
                or CryptographicException)
        {
            return VerificationFailure(
                backupId,
                "backup_verification_io_failed",
                "The backup could not be read and verified.");
        }
    }

    private async Task<BackupManifest> ReadRetentionManifestAsync(
        string backupPath,
        BackupRetentionCandidate candidate,
        CancellationToken cancellationToken)
    {
        var manifestPath = ResolveUnderRoot(backupPath, ManifestFileName);
        var sidecarPath = ResolveUnderRoot(backupPath, ManifestHashFileName);
        EnsurePathChainHasNoReparsePoints(
            backupPath,
            manifestPath,
            requireLeaf: true);
        EnsurePathChainHasNoReparsePoints(
            backupPath,
            sidecarPath,
            requireLeaf: true);
        if (!File.Exists(manifestPath) || !File.Exists(sidecarPath))
        {
            throw new InvalidDataException(
                "The backup retention manifest is missing.");
        }

        var manifestInfo = new FileInfo(manifestPath);
        if (manifestInfo.Length > _options.MaximumManifestBytes)
        {
            throw new InvalidDataException(
                "The backup retention manifest is too large.");
        }

        var manifestBytes = await File.ReadAllBytesAsync(
                manifestPath,
                cancellationToken)
            .ConfigureAwait(false);
        var actualManifestSha256 = Sha256(manifestBytes);
        var sidecarSha256 = await ReadSidecarHashAsync(
                sidecarPath,
                cancellationToken)
            .ConfigureAwait(false);
        if (!FixedTimeHexEquals(
                actualManifestSha256,
                candidate.ManifestSha256)
            || !FixedTimeHexEquals(actualManifestSha256, sidecarSha256))
        {
            throw new InvalidDataException(
                "The backup retention manifest hash does not match.");
        }

        var manifest = JsonSerializer.Deserialize(
            manifestBytes,
            BackupJsonContext.Default.BackupManifest);
        if (manifest is null
            || manifest.FormatVersion != 1
            || !string.Equals(
                manifest.Product,
                "OokiGrader",
                StringComparison.Ordinal)
            || !string.Equals(
                manifest.BackupId,
                candidate.BackupId,
                StringComparison.Ordinal)
            || manifest.Files.Count == 0
            || manifest.Files.Count > _options.MaximumManifestEntries)
        {
            throw new InvalidDataException(
                "The backup retention manifest is invalid.");
        }

        return manifest;
    }

    private static IEnumerable<RetentionMetadata> SelectExpiredBackups(
        IReadOnlyList<RetentionMetadata> candidates,
        string newestBackupId,
        DateTimeOffset now,
        int dailyRetentionDays,
        int weeklyRetentionWeeks,
        int monthlyRetentionMonths)
    {
        var dailyCutoff = now.AddDays(-dailyRetentionDays);
        var weeklyCutoff = now.AddDays(checked(-7 * weeklyRetentionWeeks));
        var monthlyCutoff = now.AddMonths(-monthlyRetentionMonths);
        var ordered = candidates
            .OrderByDescending(item => item.Candidate.CompletedAt)
            .ThenByDescending(
                item => item.Candidate.BackupId,
                StringComparer.Ordinal)
            .ToArray();
        var keep = new HashSet<string>(StringComparer.Ordinal)
        {
            newestBackupId,
        };

        foreach (var item in ordered)
        {
            var completedAt = item.Candidate.CompletedAt;
            if (!item.Missing && completedAt >= dailyCutoff)
            {
                keep.Add(item.Candidate.BackupId);
            }
        }

        foreach (var weeklyGroup in ordered
            .Where(item =>
                !item.Missing
                && item.Candidate.CompletedAt < dailyCutoff
                && item.Candidate.CompletedAt >= weeklyCutoff)
            .GroupBy(item => (
                ISOWeek.GetYear(item.Candidate.CompletedAt.UtcDateTime),
                ISOWeek.GetWeekOfYear(item.Candidate.CompletedAt.UtcDateTime))))
        {
            keep.Add(weeklyGroup.First().Candidate.BackupId);
        }

        foreach (var monthlyGroup in ordered
            .Where(item =>
                !item.Missing
                && item.Candidate.CompletedAt < weeklyCutoff
                && item.Candidate.CompletedAt >= monthlyCutoff)
            .GroupBy(item => (
                item.Candidate.CompletedAt.UtcDateTime.Year,
                item.Candidate.CompletedAt.UtcDateTime.Month)))
        {
            keep.Add(monthlyGroup.First().Candidate.BackupId);
        }

        foreach (var item in ordered)
        {
            if (string.Equals(
                item.Candidate.BackupId,
                newestBackupId,
                StringComparison.Ordinal))
            {
                continue;
            }

            var scanLifetimeExpired = item.ManagedScansIncluded
                && now - item.Candidate.CompletedAt
                    >= MaximumScanBackupLifetime;
            if (scanLifetimeExpired
                || !keep.Contains(item.Candidate.BackupId))
            {
                yield return item;
            }
        }
    }

    private string ResolveValidatedBackupSetPath(
        string destinationRoot,
        BackupRetentionCandidate candidate)
    {
        var normalized = NormalizeManifestPath(
            candidate.DestinationRelativePath);
        if (candidate.DestinationRelativePath.Contains(
                '\\',
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Backup set paths must use canonical separators.");
        }

        var segments = normalized.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 4
            || !string.Equals(
                normalized,
                string.Join('/', segments),
                StringComparison.Ordinal)
            || !string.Equals(segments[0], "sets", StringComparison.Ordinal)
            || segments[1].Length != 4
            || !int.TryParse(
                segments[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out _)
            || segments[2].Length != 2
            || !int.TryParse(
                segments[2],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var month)
            || month is < 1 or > 12
            || !string.Equals(
                segments[3],
                candidate.BackupId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The backup set path is not canonical.");
        }

        return ResolveUnderRoot(destinationRoot, normalized);
    }

    private static void ValidateRetentionCandidate(
        BackupRetentionCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ValidateBackupId(candidate.BackupId);
        if (!IsSha256(candidate.ManifestSha256)
            || string.IsNullOrWhiteSpace(candidate.DestinationRelativePath))
        {
            throw new ArgumentException(
                "The backup retention candidate is invalid.",
                nameof(candidate));
        }
    }

    private void DeleteBackupSet(
        string destinationRoot,
        string backupPath,
        string backupId)
    {
        var expectedLeaf = Path.GetFileName(
            Path.TrimEndingDirectorySeparator(backupPath));
        if (!string.Equals(expectedLeaf, backupId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The backup deletion target identifier is invalid.");
        }

        EnsurePathChainHasNoReparsePoints(
            destinationRoot,
            backupPath,
            requireLeaf: true);
        var directories = new Stack<string>();
        directories.Push(backupPath);
        var inspectedEntries = 0;
        var maximumEntries = checked(
            _options.MaximumManifestEntries * 2 + 64);
        while (directories.TryPop(out var directory))
        {
            EnsureNotReparsePoint(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(
                directory,
                "*",
                SearchOption.TopDirectoryOnly))
            {
                inspectedEntries++;
                if (inspectedEntries > maximumEntries)
                {
                    throw new InvalidOperationException(
                        "The backup set exceeded the retention traversal bound.");
                }

                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException(
                        "Backup retention does not traverse reparse points.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Push(path);
                }
            }
        }

        EnsurePathChainHasNoReparsePoints(
            destinationRoot,
            backupPath,
            requireLeaf: true);
        Directory.Delete(backupPath, recursive: true);
    }

    private void EnsurePathChainHasNoReparsePoints(
        string root,
        string path,
        bool requireLeaf)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(root));
        var normalizedPath = Path.GetFullPath(path);
        if (!string.Equals(normalizedRoot, normalizedPath, _pathComparison)
            && !normalizedPath.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                _pathComparison))
        {
            throw new InvalidOperationException(
                "The backup path escaped its configured root.");
        }

        if (!Directory.Exists(normalizedRoot))
        {
            throw new DirectoryNotFoundException(
                "The configured backup root is unavailable.");
        }

        EnsureNotReparsePoint(normalizedRoot);
        var relativePath = Path.GetRelativePath(
            normalizedRoot,
            normalizedPath);
        if (relativePath == ".")
        {
            return;
        }

        var current = normalizedRoot;
        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            var exists = File.Exists(current) || Directory.Exists(current);
            if (!exists)
            {
                if (requireLeaf)
                {
                    throw new FileNotFoundException(
                        "A backup path component is unavailable.");
                }

                return;
            }

            EnsureNotReparsePoint(current);
        }
    }

    private static BackupRetentionFailure RetentionFailure(
        string backupId,
        string errorCode,
        string safeDetail) =>
        new(backupId, errorCode, safeDetail);

    private async Task<BackupManifestEntry> CopyContentObjectAsync(
        SnapshotContentObject contentObject,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<ContentStorageClass>(
                contentObject.StorageClass,
                ignoreCase: false,
                out var storageClass))
        {
            throw new BackupOperationException(
                "backup_storage_class_invalid",
                "The database contains an unsupported backup storage class.");
        }

        var locator = new ContentObjectLocator(
            storageClass,
            contentObject.Sha256,
            contentObject.Bytes,
            contentObject.Extension);
        var relativePath = NormalizeAndValidateObjectPath(
            contentObject.RelativeObjectPath);
        var backupRelativePath = NormalizeManifestPath(
            Path.Combine("objects", relativePath));
        var destinationPath = ResolveUnderRoot(stagingRoot, backupRelativePath);
        await using var source = await _contentStore
            .OpenReadAsync(locator, cancellationToken)
            .ConfigureAwait(false);
        var copiedHash = await CopyAndHashAsync(
                source,
                destinationPath,
                cancellationToken)
            .ConfigureAwait(false);
        if (!FixedTimeHexEquals(copiedHash, contentObject.Sha256))
        {
            throw new BackupOperationException(
                "backup_source_object_hash_mismatch",
                "A source object no longer matches its database SHA-256.");
        }

        return new BackupManifestEntry(
            "content_object",
            backupRelativePath,
            copiedHash,
            contentObject.Bytes,
            contentObject.Id,
            contentObject.StorageClass,
            contentObject.RetentionClass);
    }

    private async Task<BackupManifestEntry> CopySecretEnvelopeAsync(
        string sourcePath,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        var secretRoot = _secretEnvelopeRootPath!;
        EnsureNotReparsePoint(sourcePath);
        var relativePath = Path.GetRelativePath(secretRoot, sourcePath);
        if (!IsSafeRelativePath(relativePath))
        {
            throw new BackupOperationException(
                "backup_secret_path_invalid",
                "A protected secret envelope path is invalid.");
        }

        var backupRelativePath = NormalizeManifestPath(
            Path.Combine("secrets", relativePath));
        var destinationPath = ResolveUnderRoot(stagingRoot, backupRelativePath);
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await CopyAndHashAsync(
                source,
                destinationPath,
                cancellationToken)
            .ConfigureAwait(false);
        return new BackupManifestEntry(
            "protected_secret_envelope",
            backupRelativePath,
            hash,
            new FileInfo(destinationPath).Length);
    }

    private IEnumerable<string> EnumerateSecretEnvelopes(string root)
    {
        EnsureNotReparsePoint(root);
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(
            root,
            "*.secret",
            new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint,
                MatchCasing = MatchCasing.CaseInsensitive,
            }))
        {
            count++;
            EnsureManifestCapacity(count);
            yield return path;
        }
    }

    private async Task<SnapshotMetadata> ReadSnapshotMetadataAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            ForeignKeys = true,
            Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var integrityResult = await CheckDatabaseIntegrityAsync(
                connection,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(integrityResult, "ok", StringComparison.Ordinal))
        {
            throw new BackupOperationException(
                "backup_database_integrity_failed",
                "The online database snapshot failed SQLite integrity checks.");
        }

        var dataVersion = await ReadPragmaLongAsync(
                connection,
                "data_version",
                cancellationToken)
            .ConfigureAwait(false);
        var schemaVersion = checked((int)await ReadPragmaLongAsync(
                connection,
                "schema_version",
                cancellationToken)
            .ConfigureAwait(false));
        var pageCount = await ReadPragmaLongAsync(
                connection,
                "page_count",
                cancellationToken)
            .ConfigureAwait(false);
        var migrationId = await ReadLatestMigrationIdAsync(
                connection,
                cancellationToken)
            .ConfigureAwait(false);

        var contentObjects = new List<SnapshotContentObject>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, sha256, bytes, extension, relative_object_path,
                   storage_class, retention_class, managed_scan_bytes
            FROM file_object
            WHERE state = 'available'
              AND (
                    ($include_scans = 1 AND managed_scan_bytes = 1)
                    OR (
                        managed_scan_bytes = 0
                        AND (
                            storage_class = 'TemplateSource'
                            OR storage_class = 'TemplateDerived'
                            OR ($include_reports = 1 AND storage_class = 'ResultReport')
                        )
                    )
                  )
            ORDER BY storage_class, sha256, id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue(
            "$include_scans",
            _options.IncludeManagedScans ? 1 : 0);
        command.Parameters.AddWithValue(
            "$include_reports",
            _options.IncludeReports ? 1 : 0);
        command.Parameters.AddWithValue(
            "$limit",
            checked(_options.MaximumManifestEntries + 1));
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (contentObjects.Count >= _options.MaximumManifestEntries - 1)
            {
                throw new BackupOperationException(
                    "backup_manifest_entry_limit",
                    "The backup contains more files than the configured manifest bound.");
            }

            var storageClass = reader.GetString(5);
            var managedScanBytes = reader.GetBoolean(7);
            if (!Enum.TryParse<ContentStorageClass>(
                    storageClass,
                    ignoreCase: false,
                    out var parsed)
                || (managedScanBytes
                    && (parsed is not ContentStorageClass.ManagedScanOriginal
                        and not ContentStorageClass.ManagedScanDerived))
                || (!managedScanBytes && !DefaultStorageClasses.Contains(parsed)))
            {
                throw new BackupOperationException(
                    "backup_storage_class_invalid",
                    "The database selected an unsupported backup storage class.");
            }

            contentObjects.Add(new SnapshotContentObject(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetString(4),
                storageClass,
                reader.GetString(6),
                managedScanBytes));
        }

        return new SnapshotMetadata(
            _timeProvider.GetUtcNow(),
            migrationId,
            dataVersion,
            schemaVersion,
            pageCount,
            contentObjects);
    }

    private Task CreateOnlineSnapshotAsync(
        string destinationPath,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                ForeignKeys = true,
                Pooling = false,
                DefaultTimeout = 30,
            }.ToString();
            var destinationConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = destinationPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                ForeignKeys = true,
                Pooling = false,
                DefaultTimeout = 30,
            }.ToString();
            using var source = new SqliteConnection(sourceConnectionString);
            using var destination = new SqliteConnection(destinationConnectionString);
            source.Open();
            destination.Open();
            source.BackupDatabase(destination);
        }, cancellationToken);
    }

    private async Task<BackupCreationResult> ReadExistingCreationResultAsync(
        string finalPath,
        string finalRelativePath,
        BackupVerificationResult verification,
        CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestAsync(finalPath, cancellationToken)
            .ConfigureAwait(false);
        var database = manifest.Files.Single(file => file.Role == "database");
        var objects = manifest.Files
            .Where(file => file.Role == "content_object")
            .ToArray();
        var secrets = manifest.Files
            .Where(file => file.Role == "protected_secret_envelope")
            .ToArray();
        return new BackupCreationResult(
            manifest.BackupId,
            NormalizeManifestPath(finalRelativePath),
            verification.ManifestSha256,
            database.Sha256,
            database.Bytes,
            objects.Length,
            objects.Sum(file => file.Bytes),
            secrets.Length,
            secrets.Sum(file => file.Bytes),
            manifest.DatabaseMigrationId,
            manifest.DatabaseDataVersion,
            manifest.CompletedAt,
            verification);
    }

    private async Task<BackupManifest> ReadManifestAsync(
        string backupPath,
        CancellationToken cancellationToken)
    {
        var manifestPath = ResolveUnderRoot(backupPath, ManifestFileName);
        if (new FileInfo(manifestPath).Length > _options.MaximumManifestBytes)
        {
            throw new InvalidDataException("The backup manifest is too large.");
        }

        var bytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken)
            .ConfigureAwait(false);
        return JsonSerializer.Deserialize(
            bytes,
            BackupJsonContext.Default.BackupManifest)
            ?? throw new InvalidDataException("The backup manifest is invalid.");
    }

    private static async Task<string> CopyAndHashAsync(
        Stream source,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous
                    | FileOptions.SequentialScan
                    | FileOptions.WriteThrough);
            while (true)
            {
                var read = await source.ReadAsync(
                    buffer.AsMemory(0, buffer.Length),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken).ConfigureAwait(false);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
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
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(0, buffer.Length),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
            }

            return Convert.ToHexString(hash.GetHashAndReset())
                .ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static async Task<string> CheckDatabaseIntegrityAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            ForeignKeys = true,
            Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await CheckDatabaseIntegrityAsync(connection, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<string> CheckDatabaseIntegrityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var integrity = connection.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check;";
        var rows = new List<string>(capacity: 4);
        await using (var reader = await integrity
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (rows.Count == 16)
                {
                    return "integrity_check_returned_too_many_rows";
                }

                rows.Add(reader.GetString(0));
            }
        }

        if (rows.Count != 1
            || !string.Equals(rows[0], "ok", StringComparison.Ordinal))
        {
            return rows.Count == 0
                ? "integrity_check_returned_no_result"
                : string.Join("; ", rows.Select(BoundIntegrityMessage));
        }

        await using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_key_check;";
        await using var foreignKeyReader = await foreignKeys
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await foreignKeyReader
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false)
            ? "foreign_key_check_failed"
            : "ok";
    }

    private static async Task<string?> ReadLatestMigrationIdAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadLatestMigrationIdAsync(connection, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<string?> ReadLatestMigrationIdAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT MigrationId
            FROM "__EFMigrationsHistory"
            ORDER BY MigrationId DESC
            LIMIT 1;
            """;
        try
        {
            return (string?)await command
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SqliteException exception) when (
            exception.SqliteErrorCode == 1)
        {
            return null;
        }
    }

    private static async Task<long> ReadPragmaLongAsync(
        SqliteConnection connection,
        string pragma,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {pragma};";
        var value = await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<string> ReadSidecarHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length > 256)
        {
            return string.Empty;
        }

        var text = await File.ReadAllTextAsync(
                path,
                Encoding.ASCII,
                cancellationToken)
            .ConfigureAwait(false);
        var separator = text.IndexOfAny([' ', '\t', '\r', '\n']);
        return (separator < 0 ? text : text[..separator]).Trim();
    }

    private BackupConfigurationStatus Status(
        bool configured,
        bool accessible,
        string? errorCode) =>
        new(
            _options.Enabled,
            configured,
            _options.DestinationEncryptionConfirmed,
            accessible,
            _destinationRootPath,
            _options.IncludeManagedScans,
            _options.ScheduleLocalHour,
            _options.ScheduleLocalMinute,
            errorCode);

    private string RequireUsableDestination(bool requireEnabled)
    {
        var status = GetConfigurationStatus();
        if (requireEnabled && !status.Enabled)
        {
            throw new BackupOperationException(
                "backup_disabled",
                "Scheduled backups are disabled.",
                isConfigurationError: true);
        }

        if (!status.Configured)
        {
            throw new BackupOperationException(
                "backup_destination_not_configured",
                "A backup destination has not been configured.",
                isConfigurationError: true);
        }

        if (!status.EncryptionConfirmed)
        {
            throw new BackupOperationException(
                "backup_destination_encryption_unconfirmed",
                "The backup destination must be confirmed as encrypted.",
                isConfigurationError: true);
        }

        if (!status.DestinationAccessible)
        {
            throw new BackupOperationException(
                "backup_destination_unavailable",
                "The backup destination is unavailable.",
                isConfigurationError: true);
        }

        return _destinationRootPath!;
    }

    private string RequireRetentionDestination()
    {
        if (_destinationRootPath is null)
        {
            throw new BackupOperationException(
                "backup_destination_not_configured",
                "A backup destination has not been configured.",
                isConfigurationError: true);
        }

        if (!Directory.Exists(_destinationRootPath))
        {
            throw new BackupOperationException(
                "backup_destination_unavailable",
                "The backup destination is unavailable.",
                isConfigurationError: true);
        }

        try
        {
            EnsureNotReparsePoint(_destinationRootPath);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            throw new BackupOperationException(
                "backup_destination_unavailable",
                "The backup destination is unavailable.",
                isConfigurationError: true,
                exception);
        }

        return _destinationRootPath;
    }

    private string NormalizeAndValidateObjectPath(string value)
    {
        if (!IsSafeRelativePath(value))
        {
            throw new BackupOperationException(
                "backup_object_path_invalid",
                "A content object path is invalid.");
        }

        var resolved = Path.GetFullPath(Path.Combine(_contentRootPath, value));
        var prefix = _contentRootPath + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, _pathComparison))
        {
            throw new BackupOperationException(
                "backup_object_path_invalid",
                "A content object path escaped its managed root.");
        }

        return value;
    }

    private string ResolveUnderRoot(string root, string relativePath)
    {
        if (!IsSafeRelativePath(relativePath))
        {
            throw new InvalidOperationException("The relative backup path is invalid.");
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(root));
        var resolved = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, _pathComparison))
        {
            throw new InvalidOperationException(
                "The backup path escaped its configured root.");
        }

        return resolved;
    }

    private static bool IsSafeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 2_048
            || Path.IsPathFullyQualified(value)
            || value.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        var segments = value.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0
            && segments.All(segment =>
                segment is not "." and not ".."
                && segment.Length <= 255);
    }

    private static string NormalizeManifestPath(string value) =>
        value.Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

    private static string BuildFinalRelativePath(
        string backupId,
        DateTimeOffset timestamp) =>
        Path.Combine(
            "sets",
            timestamp.UtcDateTime.ToString("yyyy", CultureInfo.InvariantCulture),
            timestamp.UtcDateTime.ToString("MM", CultureInfo.InvariantCulture),
            backupId);

    private void PrepareEmptyStagingDirectory(
        string stagingPath,
        string destinationRoot)
    {
        if (Directory.Exists(stagingPath))
        {
            TryDeleteStagingDirectory(stagingPath, destinationRoot);
        }

        Directory.CreateDirectory(stagingPath);
        EnsureNotReparsePoint(stagingPath);
    }

    private void TryDeleteStagingDirectory(
        string stagingPath,
        string destinationRoot)
    {
        try
        {
            var expectedParent = ResolveUnderRoot(destinationRoot, ".staging");
            var actualParent = Path.GetDirectoryName(Path.GetFullPath(stagingPath));
            if (string.Equals(actualParent, expectedParent, _pathComparison)
                && Directory.Exists(stagingPath))
            {
                EnsureNotReparsePoint(stagingPath);
                Directory.Delete(stagingPath, recursive: true);
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {
        }
    }

    private void EnsureManifestCapacity(int desiredCount)
    {
        if (desiredCount > _options.MaximumManifestEntries)
        {
            throw new BackupOperationException(
                "backup_manifest_entry_limit",
                "The backup contains more files than the configured manifest bound.");
        }
    }

    private BackupVerificationResult VerificationFailure(
        string backupId,
        string errorCode,
        string safeDetail,
        string manifestSha256 = "",
        string integrityResult = "not_checked") =>
        new(
            Verified: false,
            backupId,
            manifestSha256,
            _timeProvider.GetUtcNow(),
            integrityResult,
            VerifiedFileCount: 0,
            VerifiedBytes: 0,
            DatabaseMigrationId: null,
            errorCode,
            safeDetail);

    private static string BoundIntegrityMessage(string value) =>
        value.Length <= 200 ? value : value[..200];

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool FixedTimeHexEquals(string left, string right)
    {
        if (!IsSha256(left) || !IsSha256(right))
        {
            return false;
        }

        var leftBytes = Convert.FromHexString(left);
        var rightBytes = Convert.FromHexString(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static bool IsSha256(string value) =>
        value.Length == 64
        && value.All(character =>
            character is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F');

    private static void ValidateBackupId(string backupId)
    {
        if (!UlidId.IsCanonical(backupId))
        {
            throw new ArgumentException(
                "A canonical ULID backup identifier is required.",
                nameof(backupId));
        }
    }

    private static void ValidateTrigger(string trigger)
    {
        if (trigger is not ("manual" or "scheduled" or "pre_upgrade"))
        {
            throw new ArgumentException(
                "The backup trigger is invalid.",
                nameof(trigger));
        }
    }

    private static void ValidateLimits(BackupOptions options)
    {
        if (options.ScheduleLocalHour is < 0 or > 23
            || options.ScheduleLocalMinute is < 0 or > 59
            || options.MaximumManifestEntries is < 1 or > 1_000_000
            || options.MaximumManifestBytes is < 1_024 or > 64 * 1024 * 1024
            || options.DailyRetentionDays is < 1 or > 365
            || options.WeeklyRetentionWeeks is < 1 or > 520
            || options.MonthlyRetentionMonths is < 1 or > 1_200
            || options.MaximumRetentionCandidates is < 1 or > 100_000
            || options.MaximumRetentionDeletesPerRun is < 1 or > 10_000
            || options.MaximumRetentionDeletesPerRun
                > options.MaximumRetentionCandidates)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Backup schedule, retention, or manifest bounds are invalid.");
        }
    }

    private static string NormalizeRequiredPath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException(
                "An absolute path is required.",
                parameterName);
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string? NormalizeOptionalPath(
        string? path,
        string parameterName) =>
        string.IsNullOrWhiteSpace(path)
            ? null
            : NormalizeRequiredPath(path, parameterName);

    private static void RejectFilesystemRoot(string path, string parameterName)
    {
        var root = Path.GetPathRoot(path);
        if (root is not null
            && string.Equals(
                Path.TrimEndingDirectorySeparator(path),
                Path.TrimEndingDirectorySeparator(root),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The filesystem root cannot be used directly as backup destination.",
                parameterName);
        }
    }

    private bool IsSameOrNestedPath(string candidate, string parent)
    {
        if (string.Equals(candidate, parent, _pathComparison))
        {
            return true;
        }

        var prefix = parent + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, _pathComparison);
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                "Backup paths may not traverse reparse points or symbolic links.");
        }
    }

    private sealed record SnapshotMetadata(
        DateTimeOffset SnapshotCreatedAt,
        string? DatabaseMigrationId,
        long DatabaseDataVersion,
        int DatabaseSchemaVersion,
        long DatabasePageCount,
        IReadOnlyList<SnapshotContentObject> ContentObjects);

    private sealed record SnapshotContentObject(
        string Id,
        string Sha256,
        long Bytes,
        string Extension,
        string RelativeObjectPath,
        string StorageClass,
        string RetentionClass,
        bool ManagedScanBytes);

    private sealed record RetentionMetadata(
        BackupRetentionCandidate Candidate,
        string BackupPath,
        bool ManagedScansIncluded,
        bool Missing);
}
