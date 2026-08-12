using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Domain.Grading;
using OokiGrader.Domain.Templates;
using OokiGrader.Host.Uploads;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Services;

public sealed record CreateOrderedScanBatchItem(
    string ClientItemId,
    string FileName,
    int InputOrdinal);

public sealed record CreateOrderedScanBatchCommand(
    string TestSessionId,
    IReadOnlyList<CreateOrderedScanBatchItem> Items,
    string StaffUserId,
    string? CorrelationId);

public sealed record OrderedScanBatchItemSnapshot(
    string Id,
    string ClientItemId,
    string FileName,
    int InputOrdinal,
    OrderedScanItemStatus Status,
    string? UploadId,
    int? DetectedTemplatePageNumber,
    int? ClassificationConfidenceBasisPoints,
    int? GroupOrdinal,
    string? SubmissionId,
    int? SubmissionPageNumber,
    string? IssueCode,
    long RowVersion);

public sealed record OrderedScanBatchGroupSnapshot(
    int GroupOrdinal,
    string Status,
    IReadOnlyList<string> ItemIds,
    string? SubmissionId);

public sealed record OrderedScanBatchSnapshot(
    string Id,
    string TestSessionId,
    int ExpectedPageCount,
    OrderedScanBatchStatus Status,
    string AssemblyPolicyVersion,
    string? PlanHash,
    string? LastErrorCode,
    long RowVersion,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<OrderedScanBatchItemSnapshot> Items,
    IReadOnlyList<OrderedScanBatchGroupSnapshot> Groups,
    IReadOnlyList<string> SubmissionIds,
    IReadOnlyList<OrderedScanAssemblyIssue> Issues)
{
    public int ItemCount => Items.Count;
}

public sealed class OrderedScanBatchServiceException(
    int statusCode,
    string code,
    string title,
    string detail,
    long? currentRowVersion = null) : Exception(code)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public string Title { get; } = title;
    public string Detail { get; } = detail;
    public long? CurrentRowVersion { get; } = currentRowVersion;
}

public sealed class OrderedScanBatchService(
    OokiGraderDbContext db,
    IContentStore contentStore,
    ContentObjectLockProvider contentObjectLocks,
    IPdfPageCountReader pdfPageCountReader,
    TimeProvider timeProvider)
{
    public const int MaximumSubmissionPages = 50;
    public const int MaximumItemsPerBatch = 1_000;
    public const int JobSchemaVersion = 1;
    public const string JobType = "ordered_scan.assemble";

    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web);

    public async Task<int?> TryResolveExpectedPageCountForSessionAsync(
        string testSessionId,
        CancellationToken cancellationToken)
    {
        var version = await db.TestSessions
            .AsNoTracking()
            .Where(item => item.Id == testSessionId)
            .Select(item => item.TemplateVersion)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (version is null)
        {
            return null;
        }

        try
        {
            return await ResolveExpectedPageCountAsync(version, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OrderedScanBatchServiceException)
        {
            return null;
        }
    }

    public async Task<OrderedScanBatchSnapshot> CreateAsync(
        CreateOrderedScanBatchCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.TestSessionId)
            || string.IsNullOrWhiteSpace(command.StaffUserId)
            || command.Items is null
            || command.Items.Count is 0 or > MaximumItemsPerBatch)
        {
            throw Invalid(
                "ORDERED_SCAN_BATCH_INVALID",
                "読取バッチを作成できません",
                "答案ページを1件以上選択してください。");
        }

        var session = await db.TestSessions
            .AsNoTracking()
            .Include(item => item.TemplateVersion)
            .SingleOrDefaultAsync(
                item => item.Id == command.TestSessionId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw NotFound("TEST_SESSION_NOT_FOUND");
        if (session.State != "open")
        {
            throw Conflict(
                "TEST_SESSION_NOT_OPEN",
                "答案を追加できません",
                "テスト実施を受付中にしてからアップロードしてください。");
        }

        if (!TemplateVersionUsePolicy.IsImmutablePublishedSnapshot(
                session.TemplateVersion.State))
        {
            throw Conflict(
                "TEMPLATE_VERSION_NOT_PUBLISHED",
                "このひな形は使用できません",
                "確定済みのひな形を選択してください。");
        }

        var expectedPageCount = await ResolveExpectedPageCountAsync(
                session.TemplateVersion,
                cancellationToken)
            .ConfigureAwait(false);
        ValidatePlannedItems(command.Items, expectedPageCount);

        var now = timeProvider.GetUtcNow();
        var batchId = UlidId.New(now);
        var planHash = ComputePlanHash(expectedPageCount, command.Items);
        var batch = new OrderedScanBatchEntity
        {
            Id = batchId,
            TestSessionId = session.Id,
            ExpectedPageCount = expectedPageCount,
            Status = OrderedScanBatchStatus.Draft,
            AssemblyPolicyVersion = OrderedScanAssemblyPlanner.CurrentPolicyVersion,
            PlanHash = planHash,
            CreatedByStaffUserId = command.StaffUserId,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now.AddHours(24),
        };
        db.OrderedScanBatches.Add(batch);
        foreach (var item in command.Items.OrderBy(item => item.InputOrdinal))
        {
            db.OrderedScanItems.Add(new OrderedScanItemEntity
            {
                Id = UlidId.New(now.AddTicks(item.InputOrdinal)),
                BatchId = batch.Id,
                ClientItemId = item.ClientItemId.Trim(),
                OriginalFileName = item.FileName.Trim(),
                InputOrdinal = item.InputOrdinal,
                Status = OrderedScanItemStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = UlidId.New(now.AddMilliseconds(1)),
            OccurredAt = now,
            ActorStaffUserId = command.StaffUserId,
            EventType = "ordered_scan_batch.created",
            ObjectType = "ordered_scan_batch",
            ObjectId = batch.Id,
            Outcome = "succeeded",
            CorrelationId = command.CorrelationId,
            SafeMetadataJson = JsonSerializer.Serialize(new
            {
                batch.TestSessionId,
                batch.ExpectedPageCount,
                itemCount = command.Items.Count,
                batch.PlanHash,
            }),
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await GetRequiredAsync(
                batch.Id,
                command.StaffUserId,
                isAdministrator: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<OrderedScanBatchSnapshot?> GetAsync(
        string batchId,
        string staffUserId,
        bool isAdministrator,
        CancellationToken cancellationToken)
    {
        var query = db.OrderedScanBatches
            .AsNoTracking()
            .Include(item => item.Items)
                .ThenInclude(item => item.UploadSession)
            .Where(item => item.Id == batchId);
        if (!isAdministrator)
        {
            query = query.Where(item => item.CreatedByStaffUserId == staffUserId);
        }

        var batch = await query.SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return batch is null ? null : ToSnapshot(batch);
    }

    public async Task<OrderedScanBatchSnapshot> QueueFinalizeAsync(
        string batchId,
        long expectedRowVersion,
        string staffUserId,
        bool isAdministrator,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var batch = await db.OrderedScanBatches
            .Include(item => item.Items)
            .SingleOrDefaultAsync(item => item.Id == batchId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw NotFound("ORDERED_SCAN_BATCH_NOT_FOUND");
        ValidateAccess(batch, staffUserId, isAdministrator);
        if (batch.Revision != expectedRowVersion)
        {
            throw Stale(batch.Revision);
        }

        if (batch.Status == OrderedScanBatchStatus.Processing)
        {
            return await GetRequiredAsync(
                    batch.Id,
                    staffUserId,
                    isAdministrator,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (batch.Status == OrderedScanBatchStatus.Completed)
        {
            return ToSnapshot(batch);
        }

        if (batch.Status != OrderedScanBatchStatus.Draft)
        {
            throw Conflict(
                "ORDERED_SCAN_BATCH_NOT_FINALIZABLE",
                "この読取バッチを組み立てられません",
                "最新のバッチ状態を確認してください。",
                batch.Revision);
        }

        var now = timeProvider.GetUtcNow();
        if (batch.ExpiresAt <= now)
        {
            batch.Status = OrderedScanBatchStatus.Expired;
            batch.LastErrorCode = "ORDERED_SCAN_BATCH_EXPIRED";
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            throw Conflict(
                "ORDERED_SCAN_BATCH_EXPIRED",
                "読取バッチの有効期限が切れました",
                "新しい読取バッチを作成してください。",
                batch.Revision);
        }

        if (batch.Items.Count == 0
            || batch.Items.Any(item =>
                item.Status != OrderedScanItemStatus.Uploaded
                || item.UploadSessionId is null
                || item.SourceFileReferenceId is null))
        {
            throw Conflict(
                "ORDERED_SCAN_PAGES_INCOMPLETE",
                "すべてのページが揃っていません",
                "送信に失敗したページを再送してから組み立ててください。",
                batch.Revision);
        }

        var actualPlanHash = ComputePlanHash(
            batch.ExpectedPageCount,
            batch.Items
                .OrderBy(item => item.InputOrdinal)
                .Select(item => new CreateOrderedScanBatchItem(
                    item.ClientItemId,
                    item.OriginalFileName,
                    item.InputOrdinal))
                .ToArray());
        if (!string.Equals(actualPlanHash, batch.PlanHash, StringComparison.Ordinal))
        {
            throw Conflict(
                "ORDERED_SCAN_PLAN_CHANGED",
                "読取順の記録が一致しません",
                "このバッチを取り消して、ページを並べ直してください。",
                batch.Revision);
        }

        batch.Status = OrderedScanBatchStatus.Processing;
        batch.LastErrorCode = null;
        batch.LastErrorJson = null;
        var jobId = UlidId.New(now.AddMilliseconds(1));
        db.BackgroundJobs.Add(new BackgroundJobEntity
        {
            Id = jobId,
            Type = JobType,
            SchemaVersion = JobSchemaVersion,
            DeduplicationKey = $"ordered-scan:{batch.Id}:assemble",
            Priority = 10,
            PayloadJson = JsonSerializer.Serialize(new { batchId = batch.Id }),
            State = "queued",
            MaxAttempts = 8,
            NextAttemptAt = now,
            CorrelationId = correlationId,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = UlidId.New(now.AddMilliseconds(2)),
            OccurredAt = now,
            ActorStaffUserId = staffUserId,
            EventType = "ordered_scan_batch.finalization_queued",
            ObjectType = "ordered_scan_batch",
            ObjectId = batch.Id,
            Outcome = "succeeded",
            CorrelationId = correlationId,
            SafeMetadataJson = JsonSerializer.Serialize(new
            {
                jobId,
                batch.ExpectedPageCount,
                itemCount = batch.Items.Count,
                batch.PlanHash,
            }),
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await GetRequiredAsync(
                batch.Id,
                staffUserId,
                isAdministrator,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<OrderedScanBatchSnapshot> CancelAsync(
        string batchId,
        long expectedRowVersion,
        string staffUserId,
        bool isAdministrator,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var batch = await db.OrderedScanBatches
            .Include(item => item.Items)
            .SingleOrDefaultAsync(item => item.Id == batchId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw NotFound("ORDERED_SCAN_BATCH_NOT_FOUND");
        ValidateAccess(batch, staffUserId, isAdministrator);
        if (batch.Revision != expectedRowVersion)
        {
            throw Stale(batch.Revision);
        }

        if (batch.Status == OrderedScanBatchStatus.Cancelled)
        {
            return ToSnapshot(batch);
        }

        if (batch.Status is OrderedScanBatchStatus.Completed
            or OrderedScanBatchStatus.Processing)
        {
            throw Conflict(
                "ORDERED_SCAN_BATCH_NOT_CANCELLABLE",
                "この読取バッチは取り消せません",
                "処理中または作成済みの答案は、答案画面で確認してください。",
                batch.Revision);
        }

        var releasedObjects = await ReleaseStagedReferencesAsync(
                batch,
                "ORDERED_SCAN_BATCH_CANCELLED",
                cancellationToken)
            .ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        batch.Status = OrderedScanBatchStatus.Cancelled;
        batch.CompletedAt = now;
        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = UlidId.New(now),
            OccurredAt = now,
            ActorStaffUserId = staffUserId,
            EventType = "ordered_scan_batch.cancelled",
            ObjectType = "ordered_scan_batch",
            ObjectId = batch.Id,
            Outcome = "succeeded",
            CorrelationId = correlationId,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await DeleteReleasedObjectsAsync(releasedObjects, cancellationToken)
            .ConfigureAwait(false);
        return await GetRequiredAsync(
                batch.Id,
                staffUserId,
                isAdministrator,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> ExpireAndReleaseAsync(
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var batches = await db.OrderedScanBatches
            .Include(item => item.Items)
            .Where(item => item.ExpiresAt <= now
                && (item.Status == OrderedScanBatchStatus.Draft
                    || item.Status == OrderedScanBatchStatus.NeedsReview
                    || item.Status == OrderedScanBatchStatus.Failed))
            .OrderBy(item => item.ExpiresAt)
            .ThenBy(item => item.Id)
            .Take(100)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var releasedObjects = new List<FileObjectEntity>();
        foreach (var batch in batches)
        {
            releasedObjects.AddRange(await ReleaseStagedReferencesAsync(
                    batch,
                    "ORDERED_SCAN_BATCH_EXPIRED",
                    cancellationToken)
                .ConfigureAwait(false));
            batch.Status = OrderedScanBatchStatus.Expired;
            batch.CompletedAt = now;
            batch.LastErrorCode = "ORDERED_SCAN_BATCH_EXPIRED";
        }

        if (batches.Length > 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await DeleteReleasedObjectsAsync(
                    releasedObjects,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await ReconcileReleasedObjectsAsync(cancellationToken)
            .ConfigureAwait(false);

        return batches.Length;
    }

    private async Task<IReadOnlyList<FileObjectEntity>> ReleaseStagedReferencesAsync(
        OrderedScanBatchEntity batch,
        string issueCode,
        CancellationToken cancellationToken)
    {
        var stagedItems = batch.Items
            .Where(item => item.SubmissionId is null
                && item.SourceFileReferenceId is not null)
            .ToArray();
        var referenceIds = stagedItems
            .Select(item => item.SourceFileReferenceId!)
            .ToArray();
        if (referenceIds.Length == 0)
        {
            return [];
        }

        var references = await db.FileReferences
            .Include(item => item.FileObject)
            .Where(item => referenceIds.Contains(item.Id)
                && item.OwnerType == "ordered_scan_batch"
                && item.OwnerId == batch.Id)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var releasedObjects = new List<FileObjectEntity>();
        foreach (var reference in references)
        {
            reference.FileObject.ReferenceCountCache = checked(
                reference.FileObject.ReferenceCountCache - 1);
            if (reference.FileObject.ReferenceCountCache == 0)
            {
                reference.FileObject.State = "deletion_pending";
                releasedObjects.Add(reference.FileObject);
            }

            var item = stagedItems.Single(staged =>
                staged.SourceFileReferenceId == reference.Id);
            item.SourceFileReferenceId = null;
            item.SourceSha256 = null;
            item.SourceBytes = null;
            item.UploadCompletedAt = null;
            item.Status = OrderedScanItemStatus.Rejected;
            item.IssueCode = issueCode;
            db.FileReferences.Remove(reference);
        }

        return releasedObjects;
    }

    private async Task DeleteReleasedObjectsAsync(
        IEnumerable<FileObjectEntity> fileObjects,
        CancellationToken cancellationToken)
    {
        var unique = fileObjects
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        foreach (var fileObject in unique)
        {
            var storageClass = Enum.Parse<ContentStorageClass>(
                fileObject.StorageClass,
                ignoreCase: false);
            await using var contentObjectLock = await contentObjectLocks
                .AcquireAsync(
                    storageClass,
                    fileObject.Sha256,
                    cancellationToken)
                .ConfigureAwait(false);
            var remainingReferences = await db.FileReferences
                .AsNoTracking()
                .CountAsync(
                    item => item.FileObjectId == fileObject.Id,
                    cancellationToken)
                .ConfigureAwait(false);
            if (remainingReferences > 0)
            {
                fileObject.ReferenceCountCache = remainingReferences;
                fileObject.State = "available";
                continue;
            }

            await contentStore.DeleteAsync(
                new ContentObjectLocator(
                    storageClass,
                    fileObject.Sha256,
                    fileObject.Bytes,
                    fileObject.Extension),
                cancellationToken).ConfigureAwait(false);
            fileObject.State = "deleted";
            fileObject.DeletedAt = timeProvider.GetUtcNow();
        }

        if (unique.Length > 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReconcileReleasedObjectsAsync(
        CancellationToken cancellationToken)
    {
        var storageClass = ContentStorageClass.ManagedScanOriginal.ToString();
        var candidates = await db.FileObjects
            .Where(item => item.StorageClass == storageClass
                && item.ManagedScanBytes
                && item.State == "deletion_pending"
                && item.ReferenceCountCache == 0)
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .Take(100)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var fileObject in candidates)
        {
            await using var contentObjectLock = await contentObjectLocks
                .AcquireAsync(
                    ContentStorageClass.ManagedScanOriginal,
                    fileObject.Sha256,
                    cancellationToken)
                .ConfigureAwait(false);
            var remainingReferences = await db.FileReferences
                .AsNoTracking()
                .CountAsync(
                    item => item.FileObjectId == fileObject.Id,
                    cancellationToken)
                .ConfigureAwait(false);
            if (remainingReferences > 0)
            {
                fileObject.ReferenceCountCache = remainingReferences;
                fileObject.State = "available";
                continue;
            }

            await contentStore.DeleteAsync(
                new ContentObjectLocator(
                    ContentStorageClass.ManagedScanOriginal,
                    fileObject.Sha256,
                    fileObject.Bytes,
                    fileObject.Extension),
                cancellationToken).ConfigureAwait(false);
            fileObject.State = "deleted";
            fileObject.DeletedAt = timeProvider.GetUtcNow();
        }

        if (candidates.Length > 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<int> ResolveExpectedPageCountAsync(
        TemplateVersionEntity version,
        CancellationToken cancellationToken)
    {
        if (version.ExpectedSubmissionPageCount is { } configured)
        {
            ValidateExpectedPageCount(configured);
            return configured;
        }

        var candidates = await db.TemplateSources
            .AsNoTracking()
            .Where(item => item.TemplateVersionId == version.Id
                && (item.SourceRole == "blank_test"
                    || item.SourceRole == "contains_model_answers"
                    || item.SourceRole == "contains_non_model_answers"))
            .OrderBy(item => item.Ordinal)
            .ThenBy(item => item.Id)
            .Select(item => new
            {
                item.SourceRole,
                item.UploadSessionId,
                item.FileReferenceId,
            })
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var selectedRole = candidates.Any(item => item.SourceRole == "blank_test")
            ? "blank_test"
            : candidates.Any(item => item.SourceRole == "contains_model_answers")
                ? "contains_model_answers"
                : "contains_non_model_answers";
        var selected = candidates
            .Where(item => item.SourceRole == selectedRole)
            .ToArray();
        if (selected.Length != 1 || selected[0].FileReferenceId is null)
        {
            throw Conflict(
                "TEMPLATE_SUBMISSION_PAGE_COUNT_MISSING",
                "答案のページ数を確認できません",
                "確定済みひな形に答案ページ数を設定してください。");
        }

        var reference = await db.FileReferences
            .AsNoTracking()
            .Include(item => item.FileObject)
            .SingleOrDefaultAsync(
                item => item.Id == selected[0].FileReferenceId,
                cancellationToken)
            .ConfigureAwait(false);
        if (reference?.FileObject is not { State: "available" } fileObject
            || fileObject.VerifiedMime is not (
                "application/pdf"
                or "image/png"
                or "image/jpeg"
                or "image/webp")
            || !IsValidTemplateSourceReference(
                version,
                selected[0].UploadSessionId,
                reference))
        {
            throw Conflict(
                "TEMPLATE_SUBMISSION_PAGE_COUNT_MISSING",
                "答案のページ数を確認できません",
                "確定済みひな形のPDF原稿を確認してください。");
        }

        var storageClass = Enum.Parse<ContentStorageClass>(
            fileObject.StorageClass,
            ignoreCase: false);
        int templatePageCount;
        if (fileObject.VerifiedMime == "application/pdf")
        {
            await using var source = await contentStore.OpenReadAsync(
                new ContentObjectLocator(
                    storageClass,
                    fileObject.Sha256,
                    fileObject.Bytes,
                    fileObject.Extension),
                cancellationToken).ConfigureAwait(false);
            try
            {
                templatePageCount = await pdfPageCountReader.GetPageCountAsync(
                    source,
                    MaximumSubmissionPages + 1,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (PdfPageCountException exception)
            {
                throw Conflict(
                    exception.Code,
                    "答案のページ数を確認できません",
                    "確定済みひな形のPDF原稿を確認してください。");
            }
        }
        else
        {
            templatePageCount = 1;
        }

        var testType = version.TestType ?? TestType.Other;
        var resolved = OrderedScanPageCountPolicy.Resolve(
            testType,
            templatePageCount);
        if (resolved != templatePageCount)
        {
            throw Conflict(
                "TEMPLATE_SUBMISSION_PAGE_COUNT_INCONSISTENT",
                "答案のページ数がテスト種別と一致しません",
                "ひな形を確認して作り直してください。");
        }

        ValidateExpectedPageCount(resolved);
        return resolved;
    }

    private static bool IsValidTemplateSourceReference(
        TemplateVersionEntity version,
        string uploadSessionId,
        FileReferenceEntity reference) =>
        (reference.OwnerType == "upload_session"
            && reference.OwnerId == uploadSessionId
            && reference.Purpose == "template_source"
            && reference.FileObject.StorageClass
                == nameof(ContentStorageClass.TemplateSource))
        || (version.OriginatingUnitId is { } unitId
            && reference.OwnerType == "template_generation_unit"
            && reference.OwnerId == unitId
            && reference.Purpose == "derived_source"
            && reference.FileObject.StorageClass
                == nameof(ContentStorageClass.TemplateDerived));

    private static void ValidateExpectedPageCount(int pageCount)
    {
        if (pageCount <= 0)
        {
            throw Conflict(
                "TEMPLATE_SUBMISSION_PAGE_COUNT_MISSING",
                "答案のページ数を確認できません",
                "確定済みひな形に答案ページ数を設定してください。");
        }

        if (pageCount > MaximumSubmissionPages)
        {
            throw Invalid(
                "TEMPLATE_SUBMISSION_PAGE_COUNT_UNSUPPORTED",
                "答案のページ数が上限を超えています",
                $"1答案は{MaximumSubmissionPages}ページ以下にしてください。");
        }
    }

    private static void ValidatePlannedItems(
        IReadOnlyList<CreateOrderedScanBatchItem> items,
        int expectedPageCount)
    {
        var ordered = items.OrderBy(item => item.InputOrdinal).ToArray();
        if (ordered.Any(item => item.InputOrdinal <= 0
                || string.IsNullOrWhiteSpace(item.ClientItemId)
                || item.ClientItemId.Length > 128
                || string.IsNullOrWhiteSpace(item.FileName)
                || item.FileName.Length > 500
                || item.ClientItemId.Any(char.IsControl)
                || item.FileName.Any(char.IsControl)
                || !string.Equals(
                    item.FileName.Trim(),
                    Path.GetFileName(item.FileName.Trim()),
                    StringComparison.Ordinal))
            || ordered.Select(item => item.InputOrdinal).Distinct().Count()
                != ordered.Length
            || ordered.Select(item => item.ClientItemId)
                .Distinct(StringComparer.Ordinal).Count() != ordered.Length
            || !ordered.Select(item => item.InputOrdinal)
                .SequenceEqual(Enumerable.Range(1, ordered.Length)))
        {
            throw Invalid(
                "ORDERED_SCAN_PLAN_INVALID",
                "読取順を保存できません",
                "ページを1から始まる連続した順番に並べてください。");
        }

        if (ordered.Length % expectedPageCount != 0)
        {
            throw Invalid(
                OrderedScanAssemblyIssueCodes.MissingTemplatePage,
                "答案のページが不足しています",
                $"1答案につき{expectedPageCount}ページになるように追加してください。");
        }
    }

    internal static string ComputePlanHash(
        int expectedPageCount,
        IReadOnlyList<CreateOrderedScanBatchItem> items)
    {
        var canonical = new StringBuilder();
        canonical.Append("ordered-single-page-scan-plan-v1\n");
        canonical.Append(expectedPageCount).Append('\n');
        foreach (var item in items.OrderBy(item => item.InputOrdinal))
        {
            Append(item.InputOrdinal.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            Append(item.ClientItemId.Trim());
            Append(item.FileName.Trim());
        }

        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();

        void Append(string value) => canonical
            .Append(value.Length)
            .Append(':')
            .Append(value)
            .Append('\n');
    }

    private async Task<OrderedScanBatchSnapshot> GetRequiredAsync(
        string batchId,
        string staffUserId,
        bool isAdministrator,
        CancellationToken cancellationToken) =>
        await GetAsync(
                batchId,
                staffUserId,
                isAdministrator,
                cancellationToken)
            .ConfigureAwait(false)
        ?? throw NotFound("ORDERED_SCAN_BATCH_NOT_FOUND");

    private static OrderedScanBatchSnapshot ToSnapshot(
        OrderedScanBatchEntity batch)
    {
        var items = batch.Items
            .OrderBy(item => item.InputOrdinal)
            .Select(item => new OrderedScanBatchItemSnapshot(
                item.Id,
                item.ClientItemId,
                item.OriginalFileName,
                item.InputOrdinal,
                item.Status,
                item.UploadSessionId,
                item.DetectedTemplatePageNumber,
                item.ClassificationConfidenceBasisPoints,
                item.GroupOrdinal,
                item.SubmissionId,
                item.SubmissionPageNumber,
                item.IssueCode,
                item.Revision))
            .ToArray();
        var groups = items
            .Where(item => item.GroupOrdinal is not null)
            .GroupBy(item => item.GroupOrdinal!.Value)
            .OrderBy(group => group.Key)
            .Select(group => new OrderedScanBatchGroupSnapshot(
                group.Key,
                group.Any(item => item.Status == OrderedScanItemStatus.NeedsReview)
                    ? "needsReview"
                    : group.All(item => item.Status == OrderedScanItemStatus.Grouped)
                        ? "complete"
                        : "pending",
                group.OrderBy(item => item.SubmissionPageNumber ?? int.MaxValue)
                    .ThenBy(item => item.InputOrdinal)
                    .Select(item => item.Id)
                    .ToArray(),
                group.Select(item => item.SubmissionId)
                    .FirstOrDefault(id => id is not null)))
            .ToArray();
        var submissionIds = items
            .Select(item => item.SubmissionId)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        IReadOnlyList<OrderedScanAssemblyIssue> issues = [];
        if (!string.IsNullOrWhiteSpace(batch.LastErrorJson))
        {
            try
            {
                issues = JsonSerializer.Deserialize<OrderedScanAssemblyIssue[]>(
                        batch.LastErrorJson,
                        JsonOptions)
                    ?? [];
            }
            catch (JsonException)
            {
                issues = [new OrderedScanAssemblyIssue(
                    batch.LastErrorCode ?? "ORDERED_SCAN_ASSEMBLY_FAILED",
                    null,
                    null,
                    null,
                    null,
                    "The stored ordered-scan issue detail could not be read.")];
            }
        }

        return new OrderedScanBatchSnapshot(
            batch.Id,
            batch.TestSessionId,
            batch.ExpectedPageCount,
            batch.Status,
            batch.AssemblyPolicyVersion,
            batch.PlanHash,
            batch.LastErrorCode,
            batch.Revision,
            batch.ExpiresAt,
            items,
            groups,
            submissionIds,
            issues);
    }

    private static void ValidateAccess(
        OrderedScanBatchEntity batch,
        string staffUserId,
        bool isAdministrator)
    {
        if (!isAdministrator
            && !string.Equals(
                batch.CreatedByStaffUserId,
                staffUserId,
                StringComparison.Ordinal))
        {
            throw NotFound("ORDERED_SCAN_BATCH_NOT_FOUND");
        }
    }

    private static OrderedScanBatchServiceException Invalid(
        string code,
        string title,
        string detail) =>
        new(StatusCodes.Status422UnprocessableEntity, code, title, detail);

    private static OrderedScanBatchServiceException Conflict(
        string code,
        string title,
        string detail,
        long? currentRowVersion = null) =>
        new(
            StatusCodes.Status409Conflict,
            code,
            title,
            detail,
            currentRowVersion);

    private static OrderedScanBatchServiceException NotFound(string code) =>
        new(
            StatusCodes.Status404NotFound,
            code,
            "読取バッチが見つかりません",
            "最新のテスト実施画面を開き直してください。");

    private static OrderedScanBatchServiceException Stale(long revision) =>
        new(
            StatusCodes.Status409Conflict,
            "ROW_VERSION_STALE",
            "別の操作で読取バッチが更新されました",
            "最新の状態を読み込んでから、もう一度操作してください。",
            revision);
}
