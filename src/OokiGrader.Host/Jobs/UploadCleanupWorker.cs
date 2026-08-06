using System.Security;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Host.Uploads;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Jobs;

public sealed record UploadCleanupResult(
    int ExpiredSessionCount,
    int ReconciledTrackedFileCount,
    int DeletedOrphanFileCount,
    int DeletedContentStoreTemporaryFileCount,
    int FailureCount,
    int QuarantinedPromotedObjectCount = 0,
    int RestoredPromotedObjectCount = 0,
    int DeletedQuarantinedObjectCount = 0);

public sealed partial class UploadCleanupWorker(
    IDbContextFactory<OokiGraderDbContext> dbContextFactory,
    IWriteCoordinator writeCoordinator,
    UploadLockProvider uploadLocks,
    IConfiguration configuration,
    IHostEnvironment environment,
    TimeProvider timeProvider,
    ILogger<UploadCleanupWorker> logger) : BackgroundService
{
    private const int MaximumTrackedUploadsPerPass = 500;
    private const int MaximumOrphanFilesPerPass = 500;
    private static readonly TimeSpan DefaultCleanupInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan OrphanMinimumAge = TimeSpan.FromHours(24);
    private readonly PromotedContentObjectReconciler _promotedObjectReconciler =
        new(
            dbContextFactory,
            writeCoordinator,
            configuration,
            environment,
            timeProvider);

    public async Task<UploadCleanupResult> ReconcileAsync(
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var incomingRoot = ResolveIncomingRoot();
        Directory.CreateDirectory(incomingRoot);
        RejectReparsePoint(incomingRoot);

        var expiredSessions = 0;
        var reconciledTrackedFiles = 0;
        var deletedOrphanFiles = 0;
        var deletedContentStoreTemporaryFiles = 0;
        var quarantinedPromotedObjects = 0;
        var restoredPromotedObjects = 0;
        var deletedQuarantinedObjects = 0;
        var failures = 0;

        var candidateIds = await FindTrackedCandidatesAsync(now, cancellationToken)
            .ConfigureAwait(false);
        foreach (var uploadId in candidateIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var uploadLock = await uploadLocks
                .AcquireAsync(uploadId, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                var candidate = await PrepareTrackedCleanupAsync(
                        uploadId,
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (candidate is null)
                {
                    continue;
                }

                if (candidate.TransitionedToExpired)
                {
                    expiredSessions++;
                }

                var outcome = DeleteTrackedFile(incomingRoot, candidate);
                await MarkTrackedFileReconciledAsync(
                        candidate,
                        outcome,
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
                reconciledTrackedFiles++;
            }
            catch (Exception exception) when (IsRecoverableCleanupFailure(exception))
            {
                failures++;
                LogTrackedCleanupFailure(exception, uploadId);
            }
        }

        try
        {
            deletedOrphanFiles = await DeleteOldOrphanFilesAsync(
                    incomingRoot,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverableCleanupFailure(exception))
        {
            failures++;
            LogOrphanScanFailure(exception);
        }

        try
        {
            deletedContentStoreTemporaryFiles =
                await DeleteOldContentStoreTemporaryFilesAsync(
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverableCleanupFailure(exception))
        {
            failures++;
            LogContentStoreTemporaryScanFailure(exception);
        }

        try
        {
            var promotedObjects = await _promotedObjectReconciler
                .ReconcileAsync(cancellationToken)
                .ConfigureAwait(false);
            quarantinedPromotedObjects =
                promotedObjects.QuarantinedObjectCount;
            restoredPromotedObjects = promotedObjects.RestoredObjectCount;
            deletedQuarantinedObjects =
                promotedObjects.DeletedQuarantinedObjectCount;
            failures += promotedObjects.FailureCount;
        }
        catch (Exception exception) when (IsRecoverableCleanupFailure(exception))
        {
            failures++;
            LogPromotedObjectReconciliationFailure(exception);
        }

        return new UploadCleanupResult(
            expiredSessions,
            reconciledTrackedFiles,
            deletedOrphanFiles,
            deletedContentStoreTemporaryFiles,
            failures,
            quarantinedPromotedObjects,
            restoredPromotedObjects,
            deletedQuarantinedObjects);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await ReconcileAsync(stoppingToken).ConfigureAwait(false);
                if (result.ExpiredSessionCount > 0
                    || result.ReconciledTrackedFileCount > 0
                    || result.DeletedOrphanFileCount > 0
                    || result.DeletedContentStoreTemporaryFileCount > 0
                    || result.QuarantinedPromotedObjectCount > 0
                    || result.RestoredPromotedObjectCount > 0
                    || result.DeletedQuarantinedObjectCount > 0
                    || result.FailureCount > 0)
                {
                    LogCleanupPass(
                        result.ExpiredSessionCount,
                        result.ReconciledTrackedFileCount,
                        result.DeletedOrphanFileCount,
                        result.DeletedContentStoreTemporaryFileCount,
                        result.QuarantinedPromotedObjectCount,
                        result.RestoredPromotedObjectCount,
                        result.DeletedQuarantinedObjectCount,
                        result.FailureCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogUnexpectedCleanupFailure(exception);
            }

            try
            {
                await Task.Delay(GetCleanupInterval(), stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task<List<string>> FindTrackedCandidatesAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await db.UploadSessions
            .AsNoTracking()
            .Where(upload => upload.IncomingRelativePath != string.Empty
                && (upload.State == "completed"
                    || upload.State == "cancelled"
                    || upload.State == "expired"
                    || upload.State == "failed"
                    || ((upload.State == "uploading"
                            || upload.State == "finalizing"
                            || upload.State == "duplicate_pending")
                        && upload.ExpiresAt <= now)))
            .OrderBy(upload => upload.ExpiresAt)
            .ThenBy(upload => upload.Id)
            .Select(upload => upload.Id)
            .Take(MaximumTrackedUploadsPerPass)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<TrackedUploadCleanup?> PrepareTrackedCleanupAsync(
        string uploadId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var upload = await db.UploadSessions
                .SingleOrDefaultAsync(item => item.Id == uploadId, token)
                .ConfigureAwait(false);
            if (upload is null || upload.IncomingRelativePath.Length == 0)
            {
                return null;
            }

            var active = upload.State is
                "uploading" or "finalizing" or "duplicate_pending";
            if (active && upload.ExpiresAt > now)
            {
                return null;
            }

            if (!active && !IsTerminal(upload.State))
            {
                return null;
            }

            var transitionedToExpired = false;
            if (active)
            {
                var previousState = upload.State;
                upload.State = "expired";
                transitionedToExpired = true;
                db.AuditEvents.Add(new AuditEventEntity
                {
                    Id = UlidId.New(now),
                    OccurredAt = now,
                    EventType = "upload.expired",
                    ObjectType = "upload_session",
                    ObjectId = upload.Id,
                    Outcome = "succeeded",
                    ReasonCode = "temporary_retention_elapsed",
                    SafeMetadataJson = JsonSerializer.Serialize(new
                    {
                        previousState,
                        purpose = upload.Purpose,
                        expectedBytes = upload.ExpectedBytes,
                        currentBytes = upload.CurrentBytes,
                    }),
                });
                db.OutboxEvents.Add(new OutboxEventEntity
                {
                    Id = UlidId.New(now),
                    AggregateType = "upload_session",
                    AggregateId = upload.Id,
                    EventType = "upload.status",
                    SchemaVersion = 1,
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        uploadId = upload.Id,
                        state = upload.State,
                    }),
                    OccurredAt = now,
                });
                await db.SaveChangesAsync(token).ConfigureAwait(false);
            }

            return new TrackedUploadCleanup(
                upload.Id,
                upload.IncomingRelativePath,
                upload.State,
                upload.Purpose,
                transitionedToExpired);
        }, cancellationToken);
    }

    private static FileDeletionOutcome DeleteTrackedFile(
        string incomingRoot,
        TrackedUploadCleanup candidate)
    {
        var path = ResolveTrackedIncomingPath(
            incomingRoot,
            candidate.UploadId,
            candidate.RelativePath);
        if (Directory.Exists(path))
        {
            throw new IOException("The tracked incoming payload is a directory.");
        }

        if (!File.Exists(path))
        {
            return new FileDeletionOutcome(AlreadyMissing: true, Bytes: 0);
        }

        RejectReparsePoint(path);
        var bytes = new FileInfo(path).Length;
        File.Delete(path);
        return new FileDeletionOutcome(AlreadyMissing: false, bytes);
    }

    private Task MarkTrackedFileReconciledAsync(
        TrackedUploadCleanup candidate,
        FileDeletionOutcome outcome,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var upload = await db.UploadSessions
                .SingleOrDefaultAsync(item => item.Id == candidate.UploadId, token)
                .ConfigureAwait(false);
            if (upload is null
                || upload.IncomingRelativePath.Length == 0
                || !string.Equals(
                    upload.IncomingRelativePath,
                    candidate.RelativePath,
                    StringComparison.Ordinal))
            {
                return;
            }

            upload.IncomingRelativePath = string.Empty;
            db.AuditEvents.Add(new AuditEventEntity
            {
                Id = UlidId.New(now),
                OccurredAt = now,
                EventType = "upload.temporary_file_deleted",
                ObjectType = "upload_session",
                ObjectId = upload.Id,
                Outcome = "succeeded",
                ReasonCode = outcome.AlreadyMissing
                    ? "reconciled_already_missing"
                    : ReasonForState(upload.State),
                SafeMetadataJson = JsonSerializer.Serialize(new
                {
                    purpose = upload.Purpose,
                    state = upload.State,
                    alreadyMissing = outcome.AlreadyMissing,
                    bytes = outcome.Bytes,
                }),
            });
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private async Task<int> DeleteOldOrphanFilesAsync(
        string incomingRoot,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var cutoff = now - OrphanMinimumAge;
        var candidatePaths = Directory
            .EnumerateFiles(incomingRoot, "*.part", SearchOption.TopDirectoryOnly)
            .Where(path => IsCanonicalPartName(Path.GetFileName(path)))
            .Where(path => new DateTimeOffset(
                    File.GetLastWriteTimeUtc(path),
                    TimeSpan.Zero)
                <= cutoff)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Take(MaximumOrphanFilesPerPass)
            .ToList();
        var deleted = 0;

        foreach (var path in candidatePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(path);
            var uploadId = fileName[..^".part".Length];
            await using var uploadLock = await uploadLocks
                .AcquireAsync(uploadId, cancellationToken)
                .ConfigureAwait(false);

            var tracked = await IsIncomingFileTrackedAsync(
                    uploadId,
                    fileName,
                    cancellationToken)
                .ConfigureAwait(false);
            if (tracked || !File.Exists(path))
            {
                continue;
            }

            try
            {
                RejectReparsePoint(path);
                var bytes = new FileInfo(path).Length;
                File.Delete(path);
                await AddOrphanCleanupAuditAsync(
                        uploadId,
                        bytes,
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
                deleted++;
            }
            catch (Exception exception) when (IsRecoverableCleanupFailure(exception))
            {
                LogOrphanCleanupFailure(exception, fileName);
            }
        }

        return deleted;
    }

    private async Task<int> DeleteOldContentStoreTemporaryFilesAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var temporaryRoot = ResolveContentStoreTemporaryRoot();
        if (!Directory.Exists(temporaryRoot))
        {
            return 0;
        }

        RejectReparsePoint(temporaryRoot);
        var cutoff = now - OrphanMinimumAge;
        var candidatePaths = Directory
            .EnumerateFiles(temporaryRoot, "*.part", SearchOption.TopDirectoryOnly)
            .Where(path => IsCanonicalContentStoreTemporaryName(
                Path.GetFileName(path)))
            .Where(path => new DateTimeOffset(
                    File.GetLastWriteTimeUtc(path),
                    TimeSpan.Zero)
                <= cutoff)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Take(MaximumOrphanFilesPerPass)
            .ToList();
        var deleted = 0;

        foreach (var path in candidatePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(path);
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                RejectReparsePoint(path);
                var bytes = new FileInfo(path).Length;
                File.Delete(path);
                await AddContentStoreTemporaryCleanupAuditAsync(
                        fileName[..^".part".Length],
                        bytes,
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
                deleted++;
            }
            catch (Exception exception) when (IsRecoverableCleanupFailure(exception))
            {
                LogContentStoreTemporaryCleanupFailure(exception, fileName);
            }
        }

        return deleted;
    }

    private async Task<bool> IsIncomingFileTrackedAsync(
        string uploadId,
        string relativePath,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await db.UploadSessions
            .AsNoTracking()
            .AnyAsync(
                upload => upload.Id == uploadId
                    || upload.IncomingRelativePath == relativePath,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private Task AddOrphanCleanupAuditAsync(
        string uploadId,
        long bytes,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            db.AuditEvents.Add(new AuditEventEntity
            {
                Id = UlidId.New(now),
                OccurredAt = now,
                EventType = "upload.orphan_temporary_file_deleted",
                ObjectType = "incoming_file",
                ObjectId = uploadId,
                Outcome = "succeeded",
                ReasonCode = "orphan_cleanup",
                SafeMetadataJson = JsonSerializer.Serialize(new { bytes }),
            });
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task AddContentStoreTemporaryCleanupAuditAsync(
        string temporaryId,
        long bytes,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            db.AuditEvents.Add(new AuditEventEntity
            {
                Id = UlidId.New(now),
                OccurredAt = now,
                EventType = "storage.orphan_temporary_file_deleted",
                ObjectType = "content_store_temporary_file",
                ObjectId = temporaryId,
                Outcome = "succeeded",
                ReasonCode = "orphan_cleanup",
                SafeMetadataJson = JsonSerializer.Serialize(new { bytes }),
            });
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private string ResolveIncomingRoot()
    {
        var configured = configuration["Data:Incoming"] ?? ".data/incoming";
        var root = Path.IsPathFullyQualified(configured)
            ? Path.GetFullPath(configured)
            : Path.GetFullPath(configured, environment.ContentRootPath);
        var filesystemRoot = Path.TrimEndingDirectorySeparator(
            Path.GetPathRoot(root)!);
        if (string.Equals(
            Path.TrimEndingDirectorySeparator(root),
            filesystemRoot,
            PathComparison()))
        {
            throw new InvalidOperationException(
                "The incoming upload directory cannot be a filesystem root.");
        }

        return Path.TrimEndingDirectorySeparator(root);
    }

    private string ResolveContentStoreTemporaryRoot()
    {
        var configuredDataRoot = configuration["Data:Root"] ?? ".data";
        var dataRoot = Path.IsPathFullyQualified(configuredDataRoot)
            ? Path.GetFullPath(configuredDataRoot)
            : Path.GetFullPath(configuredDataRoot, environment.ContentRootPath);
        var configuredObjectStore = configuration["Data:ObjectStore"];
        var objectStoreRoot = string.IsNullOrWhiteSpace(configuredObjectStore)
            ? Path.Combine(dataRoot, "objects")
            : Path.IsPathFullyQualified(configuredObjectStore)
                ? Path.GetFullPath(configuredObjectStore)
                : Path.GetFullPath(
                    configuredObjectStore,
                    environment.ContentRootPath);
        objectStoreRoot = Path.TrimEndingDirectorySeparator(objectStoreRoot);
        var filesystemRoot = Path.TrimEndingDirectorySeparator(
            Path.GetPathRoot(objectStoreRoot)!);
        if (string.Equals(objectStoreRoot, filesystemRoot, PathComparison()))
        {
            throw new InvalidOperationException(
                "The content store cannot be a filesystem root.");
        }

        return Path.Combine(objectStoreRoot, "incoming", "objects");
    }

    private static string ResolveTrackedIncomingPath(
        string incomingRoot,
        string uploadId,
        string relativePath)
    {
        var expectedName = $"{uploadId}.part";
        if (!UlidId.IsCanonical(uploadId)
            || !string.Equals(relativePath, expectedName, PathComparison()))
        {
            throw new InvalidOperationException(
                "The tracked incoming path is not a canonical upload payload name.");
        }

        var path = Path.GetFullPath(Path.Combine(incomingRoot, relativePath));
        if (!string.Equals(
            Path.GetDirectoryName(path),
            incomingRoot,
            PathComparison()))
        {
            throw new UnauthorizedAccessException(
                "The tracked incoming path escaped its configured root.");
        }

        return path;
    }

    private static bool IsCanonicalPartName(string fileName)
    {
        return fileName.EndsWith(".part", StringComparison.Ordinal)
            && UlidId.IsCanonical(fileName[..^".part".Length]);
    }

    private static bool IsCanonicalContentStoreTemporaryName(string fileName)
    {
        return fileName.EndsWith(".part", StringComparison.Ordinal)
            && Guid.TryParseExact(
                fileName[..^".part".Length],
                "N",
                out _);
    }

    private TimeSpan GetCleanupInterval()
    {
        var minutes = configuration.GetValue(
            "Uploads:CleanupIntervalMinutes",
            (int)DefaultCleanupInterval.TotalMinutes);
        return TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 24 * 60));
    }

    private static bool IsTerminal(string state)
    {
        return state is "completed" or "cancelled" or "expired" or "failed";
    }

    private static string ReasonForState(string state)
    {
        return state switch
        {
            "completed" => "upload_completed",
            "cancelled" => "upload_cancelled",
            "expired" => "temporary_retention_elapsed",
            "failed" => "upload_failed",
            _ => "temporary_cleanup",
        };
    }

    private static bool IsRecoverableCleanupFailure(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or InvalidOperationException;
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException(
                "Incoming upload cleanup does not traverse reparse points.");
        }
    }

    private static StringComparison PathComparison()
    {
        return OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Warning,
        Message = "Tracked upload cleanup failed for upload {UploadId}.")]
    private partial void LogTrackedCleanupFailure(Exception exception, string uploadId);

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Warning,
        Message = "Orphan incoming-file scan failed.")]
    private partial void LogOrphanScanFailure(Exception exception);

    [LoggerMessage(
        EventId = 3003,
        Level = LogLevel.Warning,
        Message = "Orphan incoming-file cleanup failed for {FileName}.")]
    private partial void LogOrphanCleanupFailure(Exception exception, string fileName);

    [LoggerMessage(
        EventId = 3004,
        Level = LogLevel.Error,
        Message = "Unexpected upload cleanup worker failure.")]
    private partial void LogUnexpectedCleanupFailure(Exception exception);

    [LoggerMessage(
        EventId = 3005,
        Level = LogLevel.Information,
        Message = "Upload cleanup pass expired {ExpiredSessions} sessions, reconciled " +
            "{TrackedFiles} tracked files, deleted {OrphanFiles} orphan files and " +
            "{ContentStoreTemporaryFiles} content-store temporary files, quarantined " +
            "{QuarantinedPromotedObjects} promoted objects, restored " +
            "{RestoredPromotedObjects}, deleted {DeletedQuarantinedObjects} from " +
            "quarantine, and reported {Failures} failures.")]
    private partial void LogCleanupPass(
        int expiredSessions,
        int trackedFiles,
        int orphanFiles,
        int contentStoreTemporaryFiles,
        int quarantinedPromotedObjects,
        int restoredPromotedObjects,
        int deletedQuarantinedObjects,
        int failures);

    [LoggerMessage(
        EventId = 3006,
        Level = LogLevel.Warning,
        Message = "Content-store temporary-file scan failed.")]
    private partial void LogContentStoreTemporaryScanFailure(Exception exception);

    [LoggerMessage(
        EventId = 3007,
        Level = LogLevel.Warning,
        Message = "Content-store temporary-file cleanup failed for {FileName}.")]
    private partial void LogContentStoreTemporaryCleanupFailure(
        Exception exception,
        string fileName);

    [LoggerMessage(
        EventId = 3008,
        Level = LogLevel.Warning,
        Message = "Promoted content-object reconciliation failed.")]
    private partial void LogPromotedObjectReconciliationFailure(
        Exception exception);

    private sealed record TrackedUploadCleanup(
        string UploadId,
        string RelativePath,
        string State,
        string Purpose,
        bool TransitionedToExpired);

    private sealed record FileDeletionOutcome(bool AlreadyMissing, long Bytes);
}
