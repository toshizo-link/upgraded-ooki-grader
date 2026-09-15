using System.Security.Cryptography;
using System.Text.Json;
using ExcelDataReader.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.SchoolManager.Delivery;
using OokiGrader.SchoolManager.PassFail;

namespace OokiGrader.Host.SchoolManager;

public sealed class SchoolManagerAutomationOptions
{
    public string PassFailInputDirectory { get; set; } = string.Empty;
    public TimeSpan StableFileAge { get; set; } = TimeSpan.FromSeconds(10);
    public long MaximumWorkbookBytes { get; set; } = 20 * 1024 * 1024;
}

public sealed class PassFailImportProcessor(
    IDbContextFactory<OokiGraderDbContext> dbContextFactory,
    IWriteCoordinator writeCoordinator,
    IContentStore contentStore,
    IOptions<SchoolManagerAutomationOptions> options,
    TimeProvider timeProvider)
{
    private readonly SchoolManagerAutomationOptions _options = options.Value;

    public async Task<bool> ProcessNextAsync(
        CancellationToken cancellationToken = default)
    {
        await using var settingsDb = await dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var activationStartedAt = await settingsDb.SchoolManagerSettings
            .AsNoTracking()
            .Where(settings => settings.Enabled
                && settings.ActivationStartedAt != null)
            .Select(settings => settings.ActivationStartedAt)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (activationStartedAt is null)
        {
            return false;
        }

        var source = await FindNextSourceAsync(
                activationStartedAt.Value,
                cancellationToken)
            .ConfigureAwait(false);
        if (source is null)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        try
        {
            using var workbook = new MemoryStream(source.Bytes, writable: false);
            var table = PassFailWorkbookReader.Read(workbook);
            await using var db = await dbContextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);
            var students = await db.Students
                .AsNoTracking()
                .Where(student => student.Status == "active"
                    && student.GradeLabel != null
                    && student.SchoolClass != null)
                .OrderBy(student => student.Id)
                .Select(student => new StudentProjection(
                    student.Id,
                    student.StudentNumber,
                    student.GradeLabel!,
                    student.SchoolClass!))
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            var artifacts = new List<PassFailArtifact>();
            foreach (var student in students)
            {
                var matchingRows = table.Rows.Count(row =>
                    PassFailTableProjector.NormalizeIdentity(row.StudentNumber)
                        == PassFailTableProjector.NormalizeIdentity(student.StudentNumber)
                    && row.GradeKey == PassFailGrade.Normalize(student.GradeLabel)
                    && PassFailTableProjector.NormalizeIdentity(row.ClassLabel)
                        == PassFailTableProjector.NormalizeIdentity(student.ClassLabel));
                if (matchingRows == 0)
                {
                    continue;
                }

                var projection = PassFailTableProjector.Project(
                    table,
                    student.StudentNumber,
                    student.GradeLabel,
                    student.ClassLabel);
                var rendered = PassFailPdfRenderer.Render(projection, now);
                await using var pdf = new MemoryStream(rendered.Bytes, writable: false);
                var stored = await contentStore.PutAsync(
                        pdf,
                        ContentStorageClass.ResultReport,
                        "pdf",
                        cancellationToken)
                    .ConfigureAwait(false);
                artifacts.Add(new PassFailArtifact(student, rendered, stored));
            }

            await PersistAsync(source, table.Rows.Count, artifacts, now, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (
            exception is PassFailWorkbookException
                or PassFailProjectionException
                or ExcelReaderException
                or InvalidDataException)
        {
            await RecordFailureAsync(
                    source,
                    exception switch
                    {
                        PassFailWorkbookException workbookException =>
                            workbookException.Code,
                        PassFailProjectionException projectionException =>
                            projectionException.Code,
                        _ => "pass_fail_workbook_invalid",
                    },
                    "The pass/fail workbook did not pass privacy-safe validation.",
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
    }

    private async Task<PassFailSourceFile?> FindNextSourceAsync(
        DateTimeOffset activationStartedAt,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.PassFailInputDirectory))
        {
            return null;
        }

        Directory.CreateDirectory(_options.PassFailInputDirectory);
        var cutoff = timeProvider.GetUtcNow() - _options.StableFileAge;
        var files = new DirectoryInfo(_options.PassFailInputDirectory)
            .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
            .Where(file => file.Extension.Equals(".xls", StringComparison.OrdinalIgnoreCase)
                || file.Extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            .Where(file => file.LastWriteTimeUtc <= cutoff.UtcDateTime)
            .Where(file => file.LastWriteTimeUtc >= activationStartedAt.UtcDateTime
                || file.CreationTimeUtc >= activationStartedAt.UtcDateTime)
            .OrderBy(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Length is <= 0 || file.Length > _options.MaximumWorkbookBytes)
            {
                continue;
            }

            byte[] bytes;
            await using (var stream = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                bytes = new byte[stream.Length];
                await stream.ReadExactlyAsync(bytes, cancellationToken)
                    .ConfigureAwait(false);
            }

            file.Refresh();
            if (file.Length != bytes.LongLength
                || file.LastWriteTimeUtc > cutoff.UtcDateTime)
            {
                continue;
            }

            var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            await using var db = await dbContextFactory
                .CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);
            if (await db.PassFailImports.AnyAsync(
                    item => item.SourceSha256 == sha256,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                continue;
            }

            return new PassFailSourceFile(
                file.Name,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                sha256,
                bytes);
        }

        return null;
    }

    private Task PersistAsync(
        PassFailSourceFile source,
        int rowCount,
        IReadOnlyList<PassFailArtifact> artifacts,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            if (await db.PassFailImports.AnyAsync(
                    item => item.SourceSha256 == source.Sha256,
                    token)
                .ConfigureAwait(false))
            {
                return;
            }

            await using var transaction = await db.Database
                .BeginTransactionAsync(token)
                .ConfigureAwait(false);
            var import = new PassFailImportEntity
            {
                Id = UlidId.New(now),
                SourceSha256 = source.Sha256,
                SourceFileName = source.FileName,
                SourceLastWriteAt = source.LastWriteAt,
                State = "processed",
                RowCount = rowCount,
                DeliveryCount = artifacts.Count,
                ProcessedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.PassFailImports.Add(import);
            var ordinal = 1;
            foreach (var artifact in artifacts)
            {
                var pending = await db.GuardianDeliveries
                    .Where(item => item.Kind == "pass_fail_table"
                        && item.StudentId == artifact.Student.Id
                        && (item.State == "pending"
                            || item.State == "ready"
                            || item.State == "failed"))
                    .ToArrayAsync(token)
                    .ConfigureAwait(false);
                foreach (var older in pending)
                {
                    if (older.LastErrorCode == "message_send_outcome_unknown")
                    {
                        continue;
                    }

                    older.State = "canceled";
                    older.LastErrorCode = "newer_pass_fail_table";
                    older.SafeErrorDetail =
                        "A newer pass/fail table replaced this unsent delivery.";
                    older.UpdatedAt = now;
                }

                var latestSentAt = await db.GuardianDeliveries
                    .Where(item => item.Kind == "pass_fail_table"
                        && item.StudentId == artifact.Student.Id
                        && (item.State == "sent"
                            || item.State == "sending"
                            || item.LastErrorCode == "message_send_outcome_unknown"))
                    .OrderByDescending(item => item.SentAt ?? item.LastAttemptAt)
                    .Select(item => item.SentAt ?? item.LastAttemptAt)
                    .FirstOrDefaultAsync(token)
                    .ConfigureAwait(false);
                var deliveryId = UlidId.New(now.AddMilliseconds(ordinal++));
                var fileObject = await GetOrCreateFileObjectAsync(
                        db,
                        artifact.Stored,
                        now,
                        token)
                    .ConfigureAwait(false);
                var fileReference = new FileReferenceEntity
                {
                    Id = UlidId.New(now.AddMilliseconds(ordinal++)),
                    FileObjectId = fileObject.Id,
                    OwnerType = "guardian_delivery",
                    OwnerId = deliveryId,
                    Purpose = "pass_fail_table_pdf",
                    RetentionAnchorAt = now,
                    CreatedAt = now,
                };
                db.FileReferences.Add(fileReference);
                fileObject.ReferenceCountCache = checked(fileObject.ReferenceCountCache + 1);
                db.GuardianDeliveries.Add(new GuardianDeliveryEntity
                {
                    Id = deliveryId,
                    Kind = "pass_fail_table",
                    StudentId = artifact.Student.Id,
                    PassFailImportId = import.Id,
                    FileReferenceId = fileReference.Id,
                    SourceKey = $"{source.Sha256}:{artifact.Student.Id}",
                    AttachmentName = $"合否表_{now.ToOffset(TimeSpan.FromHours(9)):yyyyMMdd}.pdf",
                    State = "ready",
                    NotBeforeAt = GuardianDeliverySchedule.NextPassFailNotBefore(
                        now,
                        latestSentAt),
                    MaxAttempts = 5,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }

            db.AuditEvents.Add(new AuditEventEntity
            {
                Id = UlidId.New(now.AddMilliseconds(900)),
                OccurredAt = now,
                EventType = "pass_fail_table.imported",
                ObjectType = "pass_fail_import",
                ObjectId = import.Id,
                Outcome = "succeeded",
                ReasonCode = "privacy_projection_verified",
                SafeMetadataJson = JsonSerializer.Serialize(new
                {
                    import.SourceSha256,
                    import.RowCount,
                    import.DeliveryCount,
                }),
            });
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    private async Task RecordFailureAsync(
        PassFailSourceFile source,
        string errorCode,
        string safeDetail,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            if (await db.PassFailImports.AnyAsync(
                    item => item.SourceSha256 == source.Sha256,
                    token)
                .ConfigureAwait(false))
            {
                return;
            }

            db.PassFailImports.Add(new PassFailImportEntity
            {
                Id = UlidId.New(now),
                SourceSha256 = source.Sha256,
                SourceFileName = source.FileName,
                SourceLastWriteAt = source.LastWriteAt,
                State = "failed",
                ErrorCode = errorCode,
                SafeErrorDetail = safeDetail,
                CreatedAt = now,
                UpdatedAt = now,
                ProcessedAt = now,
            });
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<FileObjectEntity> GetOrCreateFileObjectAsync(
        OokiGraderDbContext db,
        ContentWriteResult stored,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var fileObject = await db.FileObjects.SingleOrDefaultAsync(
                item => item.StorageClass == ContentStorageClass.ResultReport.ToString()
                    && item.Sha256 == stored.Locator.Sha256,
                cancellationToken)
            .ConfigureAwait(false);
        if (fileObject is not null)
        {
            if (fileObject.Bytes != stored.Locator.Bytes
                || fileObject.Extension != "pdf"
                || fileObject.RelativeObjectPath != stored.RelativePath)
            {
                throw new InvalidOperationException(
                    "The pass/fail PDF content record is inconsistent.");
            }

            fileObject.State = "available";
            fileObject.VerifiedAt = now;
            fileObject.DeletedAt = null;
            return fileObject;
        }

        fileObject = new FileObjectEntity
        {
            Id = UlidId.New(now),
            Sha256 = stored.Locator.Sha256,
            Bytes = stored.Locator.Bytes,
            VerifiedMime = "application/pdf",
            Extension = "pdf",
            RelativeObjectPath = stored.RelativePath,
            StorageClass = ContentStorageClass.ResultReport.ToString(),
            RetentionClass = "result_report",
            ManagedScanBytes = false,
            State = "available",
            CreatedAt = now,
            VerifiedAt = now,
            ReferenceCountCache = 0,
        };
        db.FileObjects.Add(fileObject);
        return fileObject;
    }

    private sealed record PassFailSourceFile(
        string FileName,
        DateTimeOffset LastWriteAt,
        string Sha256,
        byte[] Bytes);

    private sealed record StudentProjection(
        string Id,
        string StudentNumber,
        string GradeLabel,
        string ClassLabel);

    private sealed record PassFailArtifact(
        StudentProjection Student,
        PassFailPdfRenderResult Rendered,
        ContentWriteResult Stored);
}
