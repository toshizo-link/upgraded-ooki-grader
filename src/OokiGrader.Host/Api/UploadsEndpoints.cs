using System.Buffers;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Contracts;
using OokiGrader.Domain.Grading;
using OokiGrader.Host.Middleware;
using OokiGrader.Host.Security;
using OokiGrader.Host.Services;
using OokiGrader.Host.Uploads;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Api;

public static partial class UploadsEndpoints
{
    private const int MaxChunkBytes = 8 * 1024 * 1024;
    private const long MaxFileBytes = 250L * 1024 * 1024;
    private static readonly string[] DuplicateActions =
        ["useExisting", "createAttempt", "cancel"];

    public static IEndpointRouteBuilder MapUploadsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/uploads")
            .WithTags("Uploads")
            .RequireAuthorization("upload");
        group.MapPost("/", CreateUpload);
        group.MapGet("/{uploadId}", GetUpload);
        group.MapMethods(
            "/{uploadId}/content",
            [HttpMethods.Head],
            HeadUpload);
        group.MapMethods(
            "/{uploadId}/content",
            [HttpMethods.Patch],
            AppendChunk)
            .AllowNonIdempotentMutation();
        group.MapPost("/{uploadId}:finalize", FinalizeUpload);
        group.MapPost("/{uploadId}:resolveDuplicate", ResolveDuplicate);
        group.MapDelete("/{uploadId}", CancelUpload);
        return endpoints;
    }

    private static async Task<IResult> CreateUpload(
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] CreateUploadRequest request,
        OokiGraderDbContext db,
        IConfiguration configuration,
        IHostEnvironment environment,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var purpose = request.Purpose switch
        {
            "completedTest" or "completed_test" => "completed_test",
            "completedTestPage" or "completed_test_page" =>
                "completed_test_page",
            "templateSource" or "template_source" => "template_source",
            _ => null,
        };
        var safeFileName = SafeFileName(request.FileName);
        var orderedPageUpload = purpose == "completed_test_page";
        var staffId = ApiHelpers.StaffId(principal);
        var elevated = principal.IsInRole("administrator")
            || principal.IsInRole("teacher");
        OrderedScanItemEntity? orderedScanPlannedItem = null;
        var orderedScanMetadataValid = orderedPageUpload
            ? !string.IsNullOrWhiteSpace(request.OrderedScanBatchId)
                && request.InputOrdinal is > 0 and <= 100_000
                && IsSafeClientItemId(request.ClientItemId)
                && string.Equals(
                    request.DeclaredMimeType,
                    "application/pdf",
                    StringComparison.OrdinalIgnoreCase)
            : request.OrderedScanBatchId is null
                && request.InputOrdinal is null
                && request.ClientItemId is null;
        if (purpose is null
            || safeFileName is null
            || request.Length is <= 0 or > MaxFileBytes
            || !AllowedDeclaredMime(request.DeclaredMimeType)
            || !orderedScanMetadataValid
            || (request.ExpectedSha256 is not null
                && !Sha256Pattern().IsMatch(request.ExpectedSha256)))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "UPLOAD_INVALID",
                "ファイルを受け付けられません",
                "形式、ファイル名、またはサイズを確認してください。");
        }

        if (purpose == "template_source"
            && !principal.IsInRole("administrator")
            && !principal.IsInRole("teacher"))
        {
            return Results.Forbid();
        }

        if (purpose is "completed_test" or "completed_test_page")
        {
            if (string.IsNullOrWhiteSpace(request.TestSessionId))
            {
                return Results.UnprocessableEntity();
            }

            var sessionState = await db.TestSessions
                .Where(session => session.Id == request.TestSessionId)
                .Select(session => session.State)
                .SingleOrDefaultAsync(cancellationToken);
            if (sessionState is null)
            {
                return Results.NotFound();
            }

            if (sessionState != "open")
            {
                return ApiHelpers.Problem(
                    context,
                    StatusCodes.Status409Conflict,
                    "TEST_SESSION_NOT_OPEN",
                    "答案を追加できません",
                    "テスト実施を受付中にしてからアップロードしてください。");
            }

            if (orderedPageUpload)
            {
                orderedScanPlannedItem = await db.OrderedScanItems
                    .Include(item => item.Batch)
                    .Include(item => item.UploadSession)
                    .SingleOrDefaultAsync(
                        item => item.BatchId == request.OrderedScanBatchId
                            && item.InputOrdinal == request.InputOrdinal
                            && item.ClientItemId == request.ClientItemId,
                        cancellationToken);
                if (orderedScanPlannedItem is null
                    || orderedScanPlannedItem.Batch.TestSessionId
                        != request.TestSessionId
                    || orderedScanPlannedItem.Batch.Status
                        != OrderedScanBatchStatus.Draft
                    || orderedScanPlannedItem.Batch.ExpiresAt
                        <= timeProvider.GetUtcNow())
                {
                    return ApiHelpers.Problem(
                        context,
                        StatusCodes.Status409Conflict,
                        "ORDERED_SCAN_BATCH_NOT_ACCEPTING_PAGES",
                        "この読取バッチには追加できません",
                        "最新の読取バッチ状態を確認してください。");
                }

                if (!elevated
                    && !string.Equals(
                        orderedScanPlannedItem.Batch.CreatedByStaffUserId,
                        staffId,
                        StringComparison.Ordinal))
                {
                    return Results.Forbid();
                }

                if (!string.Equals(
                    orderedScanPlannedItem.OriginalFileName,
                    safeFileName,
                    StringComparison.Ordinal))
                {
                    return ApiHelpers.Problem(
                        context,
                        StatusCodes.Status409Conflict,
                        "ORDERED_SCAN_MANIFEST_FILE_MISMATCH",
                        "読取計画とファイル名が一致しません",
                        "読取バッチを作り直すか、選択した元のファイルを送信してください。");
                }

                if (orderedScanPlannedItem.UploadSession is { } existingPageUpload
                    && existingPageUpload.State is
                        "uploading" or "finalizing" or "completed")
                {
                    if (!MatchesOrderedPageRetry(
                            existingPageUpload,
                            request,
                            safeFileName))
                    {
                        return ApiHelpers.Problem(
                            context,
                            StatusCodes.Status409Conflict,
                            "ORDERED_SCAN_UPLOAD_RETRY_MISMATCH",
                            "再送するファイル情報が一致しません",
                            "同じファイル情報で再開するか、読取バッチを作り直してください。");
                    }

                    return Results.Ok(ToUploadStatus(existingPageUpload));
                }

                if (orderedScanPlannedItem.UploadSession is { } replacedUpload)
                {
                    if (replacedUpload.State is not
                        ("failed" or "cancelled" or "expired"))
                    {
                        return ApiHelpers.Problem(
                            context,
                            StatusCodes.Status409Conflict,
                            "ORDERED_SCAN_UPLOAD_RETRY_BLOCKED",
                            "このページの再送を開始できません",
                            "読取バッチを再読み込みしてアップロード状態を確認してください。");
                    }

                    if (!string.IsNullOrWhiteSpace(
                            replacedUpload.IncomingRelativePath))
                    {
                        TryDeleteIncomingFile(ResolveIncomingPath(
                            ResolveIncomingRoot(configuration, environment),
                            replacedUpload.IncomingRelativePath));
                    }
                }

                if (orderedScanPlannedItem.Status
                    != OrderedScanItemStatus.Pending)
                {
                    return ApiHelpers.Problem(
                        context,
                        StatusCodes.Status409Conflict,
                        "ORDERED_SCAN_ITEM_ALREADY_STAGED",
                        "このページは送信済みです",
                        "読取バッチを再読み込みしてください。");
                }
            }
        }

        if (!await HasAdmissionCapacityAsync(
                request.Length,
                purpose,
                db,
                configuration,
                environment,
                cancellationToken))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status507InsufficientStorage,
                "STORAGE_RESERVE_REQUIRED",
                "保存容量が不足しています",
                "管理者に連絡して保存容量を確保してください。");
        }

        var idempotencyKey = context.Request.Headers["Idempotency-Key"].FirstOrDefault();
        if (idempotencyKey?.Length > 64)
        {
            return Results.BadRequest();
        }

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var existing = await db.UploadSessions
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    upload => upload.CreatedByStaffUserId == staffId
                        && upload.IdempotencyKey == idempotencyKey,
                    cancellationToken);
            if (existing is not null)
            {
                return Results.Ok(ToUploadStatus(existing));
            }
        }

        var now = timeProvider.GetUtcNow();
        var uploadId = UlidId.New(now);
        var incomingRoot = ResolveIncomingRoot(configuration, environment);
        Directory.CreateDirectory(incomingRoot);
        var relativePath = $"{uploadId}.part";
        var fullPath = ResolveIncomingPath(incomingRoot, relativePath);
        await using (File.Create(fullPath))
        {
        }

        var upload = new UploadSessionEntity
        {
            Id = uploadId,
            CreatedByStaffUserId = staffId,
            Purpose = purpose,
            TestSessionId = request.TestSessionId,
            OrderedScanBatchId = request.OrderedScanBatchId,
            OrderedScanInputOrdinal = request.InputOrdinal,
            OrderedScanClientItemId = request.ClientItemId?.Trim(),
            OriginalFileName = safeFileName,
            DeclaredMimeType = request.DeclaredMimeType.ToLowerInvariant(),
            ExpectedBytes = request.Length,
            CurrentBytes = 0,
            ExpectedSha256 = request.ExpectedSha256?.ToLowerInvariant(),
            IncomingRelativePath = relativePath,
            State = "uploading",
            ExpiresAt = now.AddHours(24),
            SourceIpPrefix = StaffAuthenticationService.ToIpPrefix(
                context.Connection.RemoteIpAddress),
            IdempotencyKey = idempotencyKey,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.UploadSessions.Add(upload);
        if (orderedPageUpload)
        {
            if (orderedScanPlannedItem is null)
            {
                File.Delete(fullPath);
                return ApiHelpers.Problem(
                    context,
                    StatusCodes.Status409Conflict,
                    "ORDERED_SCAN_PLAN_CHANGED",
                    "読取順の記録が一致しません",
                    "読取バッチを再読み込みして順番を確認してください。");
            }

            orderedScanPlannedItem.UploadSessionId = upload.Id;
        }
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            File.Delete(fullPath);
            throw;
        }

        return Results.Created($"/api/v1/uploads/{upload.Id}", ToUploadStatus(upload));
    }

    private static async Task<IResult> GetUpload(
        string uploadId,
        ClaimsPrincipal principal,
        OokiGraderDbContext db,
        CancellationToken cancellationToken)
    {
        var upload = await FindAuthorizedUpload(
            db,
            uploadId,
            principal,
            cancellationToken);
        return upload is null ? Results.NotFound() : Results.Ok(ToUploadStatus(upload));
    }

    private static async Task HeadUpload(
        string uploadId,
        HttpContext context,
        ClaimsPrincipal principal,
        OokiGraderDbContext db,
        CancellationToken cancellationToken)
    {
        var upload = await FindAuthorizedUpload(
            db,
            uploadId,
            principal,
            cancellationToken);
        if (upload is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.Headers["Upload-Offset"] =
            upload.CurrentBytes.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["Upload-Length"] =
            upload.ExpectedBytes.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["Upload-Expires"] = upload.ExpiresAt.ToString("O");
        context.Response.Headers.CacheControl = "no-store";
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private static async Task<IResult> AppendChunk(
        string uploadId,
        HttpContext context,
        ClaimsPrincipal principal,
        OokiGraderDbContext db,
        UploadLockProvider locks,
        IConfiguration configuration,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                context.Request.ContentType,
                "application/offset+octet-stream",
                StringComparison.OrdinalIgnoreCase)
            || !long.TryParse(
                context.Request.Headers["Upload-Offset"].FirstOrDefault(),
                out var requestedOffset)
            || context.Request.ContentLength is not > 0
            || context.Request.ContentLength > MaxChunkBytes)
        {
            return Results.BadRequest();
        }

        await using var uploadLock = await locks.AcquireAsync(uploadId, cancellationToken);
        var upload = await FindAuthorizedUpload(
            db,
            uploadId,
            principal,
            cancellationToken);
        if (upload is null)
        {
            return Results.NotFound();
        }

        if (upload.State != "uploading" || upload.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return Results.Conflict();
        }

        if (requestedOffset != upload.CurrentBytes)
        {
            context.Response.Headers["Upload-Offset"] =
                upload.CurrentBytes.ToString(CultureInfo.InvariantCulture);
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "UPLOAD_OFFSET_MISMATCH",
                "アップロード位置が一致しません",
                "現在位置から再開してください。");
        }

        var chunkBytes = context.Request.ContentLength.Value;
        if (upload.CurrentBytes + chunkBytes > upload.ExpectedBytes)
        {
            return Results.BadRequest();
        }

        var incomingRoot = ResolveIncomingRoot(configuration, environment);
        var path = ResolveIncomingPath(incomingRoot, upload.IncomingRelativePath);
        await using var file = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length != upload.CurrentBytes)
        {
            file.SetLength(upload.CurrentBytes);
        }

        file.Position = upload.CurrentBytes;
        var originalOffset = upload.CurrentBytes;
        try
        {
            await CopyExactAsync(
                context.Request.Body,
                file,
                chunkBytes,
                cancellationToken);
            await file.FlushAsync(cancellationToken);
            file.Flush(flushToDisk: true);
            upload.CurrentBytes = checked(upload.CurrentBytes + chunkBytes);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            file.SetLength(originalOffset);
            throw;
        }

        context.Response.Headers["Upload-Offset"] =
            upload.CurrentBytes.ToString(CultureInfo.InvariantCulture);
        return Results.NoContent();
    }

    private static async Task<IResult> FinalizeUpload(
        string uploadId,
        HttpContext context,
        ClaimsPrincipal principal,
        OokiGraderDbContext db,
        UploadLockProvider locks,
        ContentObjectLockProvider contentObjectLocks,
        IContentStore contentStore,
        [FromServices] IPdfPageCountReader pdfPageCountReader,
        IConfiguration configuration,
        IHostEnvironment environment,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        await using var uploadLock = await locks.AcquireAsync(uploadId, cancellationToken);
        var upload = await FindAuthorizedUpload(
            db,
            uploadId,
            principal,
            cancellationToken);
        if (upload is null)
        {
            return Results.NotFound();
        }

        if (upload.State == "completed")
        {
            var existingJobId = await db.BackgroundJobs
                .Where(job => job.DeduplicationKey == $"submission:{upload.DestinationId}:preprocess")
                .Select(job => job.Id)
                .SingleOrDefaultAsync(cancellationToken);
            return Results.Ok(new
            {
                uploadId = upload.Id,
                state = upload.State,
                rowVersion = upload.Revision,
                submissionId = upload.DestinationType == "submission"
                    ? upload.DestinationId
                    : null,
                orderedScanItemId = upload.DestinationType == "ordered_scan_item"
                    ? upload.DestinationId
                    : null,
                jobId = existingJobId,
                statusUrl = upload.DestinationType switch
                {
                    "submission" => $"/api/v1/submissions/{upload.DestinationId}",
                    "ordered_scan_item" =>
                        $"/api/v1/ordered-scan-batches/{upload.OrderedScanBatchId}",
                    _ => $"/api/v1/uploads/{upload.Id}",
                },
            });
        }

        if (upload.State == "duplicate_pending")
        {
            return ExactDuplicateProblem(context, upload);
        }

        if (upload.State is not ("uploading" or "finalizing")
            || upload.CurrentBytes != upload.ExpectedBytes)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "UPLOAD_INCOMPLETE",
                "アップロードが完了していません",
                $"{upload.CurrentBytes} / {upload.ExpectedBytes} バイトを受信しました。");
        }

        var incomingRoot = ResolveIncomingRoot(configuration, environment);
        var path = ResolveIncomingPath(incomingRoot, upload.IncomingRelativePath);
        await using var source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var fileType = await FileSignatureValidator.IdentifyAsync(source, cancellationToken);
        if (fileType is null)
        {
            upload.State = "failed";
            await db.SaveChangesAsync(cancellationToken);
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status415UnsupportedMediaType,
                "UPLOAD_SIGNATURE_INVALID",
                "ファイル形式を確認できません",
                "PDF、JPEG、PNG、TIFFのいずれかを選択してください。");
        }

        if (upload.Purpose == "completed_test_page")
        {
            if (fileType.MimeType != "application/pdf")
            {
                upload.State = "failed";
                await db.SaveChangesAsync(cancellationToken);
                return ApiHelpers.Problem(
                    context,
                    StatusCodes.Status415UnsupportedMediaType,
                    "ORDERED_SCAN_PAGE_MUST_BE_PDF",
                    "1ページPDFを選択してください",
                    "読取順を保持するため、各答案ページを1ページPDFとして送信してください。");
            }

            try
            {
                source.Position = 0;
                var pageCount = await pdfPageCountReader.GetPageCountAsync(
                    source,
                    maximumPages: 2,
                    cancellationToken);
                if (pageCount != 1)
                {
                    upload.State = "failed";
                    await db.SaveChangesAsync(cancellationToken);
                    return ApiHelpers.Problem(
                        context,
                        StatusCodes.Status422UnprocessableEntity,
                        "ORDERED_SCAN_PAGE_COUNT_INVALID",
                        "1ファイルにつき1ページが必要です",
                        $"このPDFには{pageCount}ページあります。1ページずつ読み取ってください。");
                }
            }
            catch (PdfPageCountException exception)
            {
                upload.State = "failed";
                await db.SaveChangesAsync(cancellationToken);
                return ApiHelpers.Problem(
                    context,
                    StatusCodes.Status422UnprocessableEntity,
                    exception.Code,
                    "PDFを確認できません",
                    "暗号化されていない1ページPDFを選択してください。");
            }
        }

        source.Position = 0;
        var incomingSha256 = Convert.ToHexString(
                await SHA256.HashDataAsync(source, cancellationToken))
            .ToLowerInvariant();
        if (upload.ExpectedSha256 is not null)
        {
            if (!string.Equals(
                    upload.ExpectedSha256,
                    incomingSha256,
                    StringComparison.Ordinal))
            {
                upload.State = "failed";
                await db.SaveChangesAsync(cancellationToken);
                return ApiHelpers.Problem(
                    context,
                    StatusCodes.Status422UnprocessableEntity,
                    "UPLOAD_HASH_MISMATCH",
                    "ファイルの整合性を確認できません",
                    "元のファイルを選び直して再送してください。");
            }
        }

        upload.State = "finalizing";
        await db.SaveChangesAsync(cancellationToken);
        source.Position = 0;
        var managedScan = upload.Purpose is
            "completed_test" or "completed_test_page";
        var storageClass = managedScan
            ? ContentStorageClass.ManagedScanOriginal
            : ContentStorageClass.TemplateSource;
        await using var contentObjectLock = await contentObjectLocks.AcquireAsync(
            storageClass,
            incomingSha256,
            cancellationToken);
        var existingState = await db.FileObjects
            .AsNoTracking()
            .Where(item => item.StorageClass == storageClass.ToString()
                && item.Sha256 == incomingSha256)
            .Select(item => item.State)
            .SingleOrDefaultAsync(cancellationToken);
        if (existingState == "deletion_pending")
        {
            context.Response.Headers.RetryAfter = "2";
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "UPLOAD_OBJECT_DELETION_IN_PROGRESS",
                "同じファイルの保存領域を整理しています",
                "数秒待ってから、同じアップロードを再開してください。");
        }

        var stored = await contentStore.PutAsync(
            source,
            storageClass,
            fileType.Extension,
            cancellationToken);
        if (!string.Equals(
                incomingSha256,
                stored.Locator.Sha256,
                StringComparison.Ordinal))
        {
            upload.State = "failed";
            await db.SaveChangesAsync(cancellationToken);
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "UPLOAD_HASH_MISMATCH",
                "ファイルの整合性を確認できません",
                "元のファイルを選び直して再送してください。");
        }

        await source.DisposeAsync();
        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var fileObject = await db.FileObjects.SingleOrDefaultAsync(
            item => item.StorageClass == storageClass.ToString()
                && item.Sha256 == stored.Locator.Sha256,
            cancellationToken);
        if (fileObject?.State == "deletion_pending")
        {
            context.Response.Headers.RetryAfter = "2";
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "UPLOAD_OBJECT_DELETION_IN_PROGRESS",
                "同じファイルの保存領域を整理しています",
                "数秒待ってから、同じアップロードを再開してください。");
        }

        if (fileObject is not null
            && fileObject.State is not ("available" or "deleted" or "missing"))
        {
            upload.State = "failed";
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "UPLOAD_OBJECT_STATE_INVALID",
                "ファイルの保存状態を確認できません",
                "元のファイルを選び直して再送してください。");
        }

        if (fileObject is null)
        {
            fileObject = new FileObjectEntity
            {
                Id = UlidId.New(now),
                Sha256 = stored.Locator.Sha256,
                Bytes = stored.Locator.Bytes,
                VerifiedMime = fileType.MimeType,
                Extension = stored.Locator.Extension,
                RelativeObjectPath = stored.RelativePath,
                StorageClass = storageClass.ToString(),
                RetentionClass = managedScan
                    ? "submitted_scan"
                    : "template_source",
                ManagedScanBytes = managedScan,
                State = "available",
                CreatedAt = now,
                VerifiedAt = now,
                ReferenceCountCache = 0,
            };
            db.FileObjects.Add(fileObject);
        }
        else if (fileObject.State is "deleted" or "missing")
        {
            // PutAsync has just atomically restored the content-addressed bytes.
            // A deletion_pending object is rejected above, so no retention
            // manifest can still be between its final reference check and the
            // physical delete while this row is revived and referenced.
            fileObject.Bytes = stored.Locator.Bytes;
            fileObject.VerifiedMime = fileType.MimeType;
            fileObject.Extension = stored.Locator.Extension;
            fileObject.RelativeObjectPath = stored.RelativePath;
            fileObject.RetentionClass = managedScan
                ? "submitted_scan"
                : "template_source";
            fileObject.ManagedScanBytes = managedScan;
            fileObject.State = "available";
            fileObject.VerifiedAt = now;
            fileObject.DeletedAt = null;
        }

        string? submissionId = null;
        string? orderedScanItemId = null;
        string? jobId = null;
        if (upload.Purpose == "completed_test")
        {
            var duplicate = await db.Submissions
                .AsNoTracking()
                .Where(item => item.TestSessionId == upload.TestSessionId
                    && item.OriginalFileObjectId == fileObject.Id
                    && item.VoidedAt == null)
                .OrderByDescending(item => item.CanonicalForSession)
                .ThenBy(item => item.CreatedAt)
                .Select(item => new
                {
                    item.Id,
                    item.AssignedStudentId,
                    item.AttemptNumber,
                })
                .FirstOrDefaultAsync(cancellationToken);
            if (duplicate is not null)
            {
                upload.FinalSha256 = stored.Locator.Sha256;
                upload.State = "duplicate_pending";
                upload.DestinationType = "duplicate_submission";
                upload.DestinationId = duplicate.Id;
                db.AuditEvents.Add(new AuditEventEntity
                {
                    Id = UlidId.New(now.AddMilliseconds(1)),
                    OccurredAt = now,
                    ActorStaffUserId = ApiHelpers.StaffId(principal),
                    EventType = "upload.duplicate_detected",
                    ObjectType = "upload_session",
                    ObjectId = upload.Id,
                    Outcome = "requires_action",
                    ReasonCode = "exact_content_match",
                    CorrelationId = context.TraceIdentifier,
                    SafeMetadataJson = JsonSerializer.Serialize(new
                    {
                        existingSubmissionId = duplicate.Id,
                        existingAttemptNumber = duplicate.AttemptNumber,
                        studentAssigned =
                            duplicate.AssignedStudentId is not null,
                    }),
                });
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                TryDeleteIncomingFile(path);
                return ExactDuplicateProblem(context, upload);
            }

            submissionId = UlidId.New(now.AddMilliseconds(1));
            var submission = new SubmissionEntity
            {
                Id = submissionId,
                TestSessionId = upload.TestSessionId!,
                State = "needs_name_review",
                ScanPayloadState = "scan_available",
                AssignmentMethod = "none",
                AttemptNumber = 1,
                CanonicalForSession = false,
                UploadedByStaffUserId = upload.CreatedByStaffUserId,
                OriginalFileName = upload.OriginalFileName,
                OriginalFileObjectId = fileObject.Id,
                UploadCompletedAt = now,
                QualitySummaryJson = """{"pipeline":"safe-ingest-v1","status":"accepted"}""",
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Submissions.Add(submission);
            jobId = UlidId.New(now.AddMilliseconds(2));
            db.BackgroundJobs.Add(new BackgroundJobEntity
            {
                Id = jobId,
                Type = "submission.preprocess",
                SchemaVersion = 1,
                DeduplicationKey = $"submission:{submissionId}:preprocess",
                Priority = 0,
                PayloadJson = JsonSerializer.Serialize(new { submissionId }),
                State = "queued",
                MaxAttempts = 8,
                NextAttemptAt = now,
                CorrelationId = context.TraceIdentifier,
                CreatedAt = now,
                UpdatedAt = now,
            });
            upload.DestinationType = "submission";
            upload.DestinationId = submissionId;
        }
        else if (upload.Purpose == "completed_test_page")
        {
            var batch = await db.OrderedScanBatches
                .SingleOrDefaultAsync(
                    item => item.Id == upload.OrderedScanBatchId
                        && item.TestSessionId == upload.TestSessionId,
                    cancellationToken);
            if (batch is null
                || batch.Status != OrderedScanBatchStatus.Draft
                || batch.ExpiresAt <= now)
            {
                upload.State = "failed";
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return ApiHelpers.Problem(
                    context,
                    StatusCodes.Status409Conflict,
                    "ORDERED_SCAN_BATCH_NOT_ACCEPTING_PAGES",
                    "この読取バッチには追加できません",
                    "最新の読取バッチ状態を確認してください。");
            }

            var plannedItem = await db.OrderedScanItems
                .SingleOrDefaultAsync(
                    item => item.BatchId == batch.Id
                        && item.InputOrdinal == upload.OrderedScanInputOrdinal
                        && item.ClientItemId == upload.OrderedScanClientItemId,
                    cancellationToken);
            if (plannedItem is null
                || plannedItem.Status != OrderedScanItemStatus.Pending
                || plannedItem.UploadSessionId != upload.Id)
            {
                upload.State = "failed";
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return ApiHelpers.Problem(
                    context,
                    StatusCodes.Status409Conflict,
                    "ORDERED_SCAN_PLAN_CHANGED",
                    "読取順の記録が一致しません",
                    "読取バッチを再読み込みして順番を確認してください。");
            }

            // Participate in the batch revision fence. Cancellation, expiry,
            // or queueing assembly updates this same concurrency token, so an
            // upload that read Draft cannot commit a new live reference after
            // one of those transitions wins.
            batch.UpdatedAt = now > batch.UpdatedAt
                ? now
                : batch.UpdatedAt.AddTicks(1);
            orderedScanItemId = plannedItem.Id;
            var stagedReference = new FileReferenceEntity
            {
                Id = UlidId.New(now.AddMilliseconds(2)),
                FileObjectId = fileObject.Id,
                OwnerType = "ordered_scan_batch",
                OwnerId = batch.Id,
                Purpose = "ordered_scan_page",
                RetentionAnchorAt = now,
                CreatedAt = now,
            };
            db.FileReferences.Add(stagedReference);
            plannedItem.SourceFileReferenceId = stagedReference.Id;
            plannedItem.SourceSha256 = stored.Locator.Sha256;
            plannedItem.SourceBytes = stored.Locator.Bytes;
            plannedItem.UploadCompletedAt = now;
            plannedItem.Status = OrderedScanItemStatus.Uploaded;
            fileObject.ReferenceCountCache = checked(
                fileObject.ReferenceCountCache + 1);
            upload.DestinationType = "ordered_scan_item";
            upload.DestinationId = orderedScanItemId;
        }
        else
        {
            upload.DestinationType = "template_source";
        }

        if (upload.Purpose != "completed_test_page")
        {
            db.FileReferences.Add(new FileReferenceEntity
            {
                Id = UlidId.New(now.AddMilliseconds(3)),
                FileObjectId = fileObject.Id,
                OwnerType = upload.Purpose == "completed_test"
                    ? "submission"
                    : "upload_session",
                OwnerId = submissionId ?? upload.Id,
                Purpose = upload.Purpose == "completed_test"
                    ? "original_scan"
                    : "template_source",
                RetentionAnchorAt = now,
                CreatedAt = now,
            });
            fileObject.ReferenceCountCache = checked(
                fileObject.ReferenceCountCache + 1);
        }
        upload.FinalSha256 = stored.Locator.Sha256;
        upload.State = "completed";
        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = UlidId.New(now.AddMilliseconds(4)),
            OccurredAt = now,
            ActorStaffUserId = ApiHelpers.StaffId(principal),
            EventType = "upload.finalized",
            ObjectType = "upload_session",
            ObjectId = upload.Id,
            Outcome = "succeeded",
            CorrelationId = context.TraceIdentifier,
            SafeMetadataJson = JsonSerializer.Serialize(new
            {
                purpose = upload.Purpose,
                orderedScanBatchId = upload.OrderedScanBatchId,
                inputOrdinal = upload.OrderedScanInputOrdinal,
                bytes = upload.ExpectedBytes,
                deduplicated = stored.Deduplicated,
            }),
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        TryDeleteIncomingFile(path);

        return Results.Accepted(
            orderedScanItemId is not null
                ? $"/api/v1/ordered-scan-batches/{upload.OrderedScanBatchId}"
                : submissionId is null
                ? $"/api/v1/uploads/{upload.Id}"
                : $"/api/v1/submissions/{submissionId}",
            new
            {
                uploadId = upload.Id,
                state = upload.State,
                rowVersion = upload.Revision,
                submissionId,
                orderedScanItemId,
                jobId,
                statusUrl = orderedScanItemId is not null
                    ? $"/api/v1/ordered-scan-batches/{upload.OrderedScanBatchId}"
                    : submissionId is null
                    ? $"/api/v1/uploads/{upload.Id}"
                    : $"/api/v1/submissions/{submissionId}",
            });
    }

    private static async Task<IResult> ResolveDuplicate(
        string uploadId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] ResolveDuplicateRequest request,
        OokiGraderDbContext db,
        UploadLockProvider locks,
        ContentObjectLockProvider contentObjectLocks,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        await using var uploadLock = await locks.AcquireAsync(
            uploadId,
            cancellationToken);
        var upload = await FindAuthorizedUpload(
            db,
            uploadId,
            principal,
            cancellationToken);
        if (upload is null)
        {
            return Results.NotFound();
        }

        if (upload.State != "duplicate_pending"
            || upload.DestinationType != "duplicate_submission"
            || string.IsNullOrWhiteSpace(upload.DestinationId))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "DUPLICATE_RESOLUTION_NOT_PENDING",
                "重複答案の確認は完了しています",
                "最新のアップロード状態を確認してください。");
        }

        if (upload.TestSessionId is not null
            && await db.TestSessions
                .AsNoTracking()
                .AnyAsync(
                    session => session.Id == upload.TestSessionId
                        && session.State == "archived",
                    cancellationToken))
        {
            return ArchivedSessionReadOnly(context);
        }

        if (request.Action is not ("useExisting" or "createAttempt" or "cancel"))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "DUPLICATE_RESOLUTION_INVALID",
                "重複答案の扱いを保存できません",
                "既存答案を使用、別の受験回として追加、または取消を選んでください。");
        }

        var existing = await db.Submissions
            .SingleOrDefaultAsync(
                item => item.Id == upload.DestinationId,
                cancellationToken);
        if (existing is null)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "DUPLICATE_SOURCE_MISSING",
                "既存の答案を確認できません",
                "管理者に連絡してください。");
        }

        var now = timeProvider.GetUtcNow();
        if (request.Action == "cancel")
        {
            upload.State = "cancelled";
            AddDuplicateResolutionAudit(
                db,
                now,
                context,
                principal,
                upload,
                existing.Id,
                "cancelled",
                null);
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        }

        if (request.Action == "useExisting")
        {
            upload.State = "completed";
            upload.DestinationType = "submission";
            upload.DestinationId = existing.Id;
            AddDuplicateResolutionAudit(
                db,
                now,
                context,
                principal,
                upload,
                existing.Id,
                "linked_existing",
                existing.Id);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(new
            {
                uploadId = upload.Id,
                state = upload.State,
                submissionId = existing.Id,
                statusUrl = $"/api/v1/submissions/{existing.Id}",
                duplicateResolution = "linkedExisting",
            });
        }

        if (!principal.IsInRole("administrator")
            && !principal.IsInRole("teacher"))
        {
            return Results.Forbid();
        }

        if (existing.OriginalFileObjectId is null)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "DUPLICATE_FILE_REFERENCE_MISSING",
                "答案ファイルを確認できません",
                "管理者に連絡してください。");
        }

        var fileObjectIdentity = await db.FileObjects
            .AsNoTracking()
            .Where(item => item.Id == existing.OriginalFileObjectId)
            .Select(item => new
            {
                item.StorageClass,
                item.Sha256,
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (fileObjectIdentity is null
            || !Enum.TryParse<ContentStorageClass>(
                fileObjectIdentity.StorageClass,
                ignoreCase: false,
                out var storageClass))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "DUPLICATE_FILE_UNAVAILABLE",
                "答案ファイルを利用できません",
                "既存答案の画像保存状態を確認してください。");
        }

        await using var contentObjectLock = await contentObjectLocks.AcquireAsync(
            storageClass,
            fileObjectIdentity.Sha256,
            cancellationToken);
        var fileObject = await db.FileObjects.SingleOrDefaultAsync(
            item => item.Id == existing.OriginalFileObjectId
                && item.State == "available",
            cancellationToken);
        if (fileObject is null)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "DUPLICATE_FILE_UNAVAILABLE",
                "答案ファイルを利用できません",
                "既存答案の画像保存状態を確認してください。");
        }

        var maximumAttempt = await db.Submissions
            .Where(item => item.TestSessionId == existing.TestSessionId
                && item.OriginalFileObjectId == existing.OriginalFileObjectId)
            .MaxAsync(item => (int?)item.AttemptNumber, cancellationToken)
            ?? existing.AttemptNumber;
        var submissionId = UlidId.New(now.AddMilliseconds(1));
        var submission = new SubmissionEntity
        {
            Id = submissionId,
            TestSessionId = existing.TestSessionId,
            State = "needs_name_review",
            ScanPayloadState = "scan_available",
            AssignmentMethod = "none",
            AttemptNumber = checked(maximumAttempt + 1),
            CanonicalForSession = false,
            UploadedByStaffUserId = upload.CreatedByStaffUserId,
            OriginalFileName = upload.OriginalFileName,
            OriginalFileObjectId = fileObject.Id,
            UploadCompletedAt = now,
            QualitySummaryJson =
                """{"pipeline":"safe-ingest-v1","status":"accepted","exactDuplicate":true}""",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Submissions.Add(submission);
        db.FileReferences.Add(new FileReferenceEntity
        {
            Id = UlidId.New(now.AddMilliseconds(2)),
            FileObjectId = fileObject.Id,
            OwnerType = "submission",
            OwnerId = submission.Id,
            Purpose = "original_scan",
            RetentionAnchorAt = now,
            CreatedAt = now,
        });
        fileObject.ReferenceCountCache = checked(
            fileObject.ReferenceCountCache + 1);
        upload.State = "completed";
        upload.DestinationType = "submission";
        upload.DestinationId = submission.Id;
        AddDuplicateResolutionAudit(
            db,
            now,
            context,
            principal,
            upload,
            existing.Id,
            "created_additional_attempt",
            submission.Id);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Accepted(
            $"/api/v1/submissions/{submission.Id}",
            new
            {
                uploadId = upload.Id,
                state = upload.State,
                submissionId = submission.Id,
                statusUrl = $"/api/v1/submissions/{submission.Id}",
                duplicateResolution = "additionalAttempt",
                submission.AttemptNumber,
            });
    }

    private static IResult ArchivedSessionReadOnly(HttpContext context) =>
        ApiHelpers.Problem(
            context,
            StatusCodes.Status409Conflict,
            "TEST_SESSION_ARCHIVED_READ_ONLY",
            "アーカイブ済みのテスト実施は変更できません",
            "過去の答案は閲覧できますが、重複答案の扱いは変更できません。");

    private static IResult ExactDuplicateProblem(
        HttpContext context,
        UploadSessionEntity upload) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            type: "https://ooki-grader.local/problems/exact-duplicate",
            title: "このファイルは同じテスト実施にアップロード済みです",
            detail: "既存答案を使用するか、先生が別の受験回として追加するか、取消を選んでください。",
            instance: context.Request.Path,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "EXACT_DUPLICATE",
                ["correlationId"] = context.TraceIdentifier,
                ["uploadId"] = upload.Id,
                ["existingSubmissionId"] = upload.DestinationId,
                ["allowedActions"] = DuplicateActions,
            });

    private static void AddDuplicateResolutionAudit(
        OokiGraderDbContext db,
        DateTimeOffset now,
        HttpContext context,
        ClaimsPrincipal principal,
        UploadSessionEntity upload,
        string existingSubmissionId,
        string reasonCode,
        string? resolvedSubmissionId)
    {
        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = UlidId.New(now.AddMilliseconds(4)),
            OccurredAt = now,
            ActorStaffUserId = ApiHelpers.StaffId(principal),
            EventType = "upload.duplicate_resolved",
            ObjectType = "upload_session",
            ObjectId = upload.Id,
            Outcome = "succeeded",
            ReasonCode = reasonCode,
            CorrelationId = context.TraceIdentifier,
            SafeMetadataJson = JsonSerializer.Serialize(new
            {
                existingSubmissionId,
                resolvedSubmissionId,
            }),
        });
    }

    private static void TryDeleteIncomingFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The acknowledged object is already durable. Scheduled temp
            // reconciliation will remove a leftover incoming file.
        }
    }

    private static async Task<IResult> CancelUpload(
        string uploadId,
        ClaimsPrincipal principal,
        OokiGraderDbContext db,
        UploadLockProvider locks,
        IConfiguration configuration,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        await using var uploadLock = await locks.AcquireAsync(uploadId, cancellationToken);
        var upload = await FindAuthorizedUpload(
            db,
            uploadId,
            principal,
            cancellationToken);
        if (upload is null)
        {
            return Results.NotFound();
        }

        if (upload.State == "completed")
        {
            return Results.Conflict();
        }

        if (upload.State is "cancelled" or "expired" or "failed")
        {
            return Results.NoContent();
        }

        upload.State = "cancelled";
        await db.SaveChangesAsync(cancellationToken);
        if (upload.IncomingRelativePath.Length == 0)
        {
            return Results.NoContent();
        }

        var path = ResolveIncomingPath(
            ResolveIncomingRoot(configuration, environment),
            upload.IncomingRelativePath);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Results.NoContent();
    }

    private static async Task<UploadSessionEntity?> FindAuthorizedUpload(
        OokiGraderDbContext db,
        string uploadId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var staffId = ApiHelpers.StaffId(principal);
        var elevated = principal.IsInRole("administrator") || principal.IsInRole("teacher");
        return await db.UploadSessions.SingleOrDefaultAsync(
            upload => upload.Id == uploadId
                && (elevated || upload.CreatedByStaffUserId == staffId),
            cancellationToken);
    }

    private static object ToUploadStatus(UploadSessionEntity upload) => new
    {
        uploadId = upload.Id,
        upload.State,
        rowVersion = upload.Revision,
        offset = upload.CurrentBytes,
        length = upload.ExpectedBytes,
        maxChunkBytes = MaxChunkBytes,
        upload.ExpiresAt,
        chunkUrl = $"/api/v1/uploads/{upload.Id}/content",
        submissionId = upload.DestinationType == "submission" ? upload.DestinationId : null,
        duplicateOfSubmissionId = upload.DestinationType == "duplicate_submission"
            ? upload.DestinationId
            : null,
        orderedScanItemId = upload.DestinationType == "ordered_scan_item"
            ? upload.DestinationId
            : null,
        upload.OrderedScanBatchId,
        inputOrdinal = upload.OrderedScanInputOrdinal,
        clientItemId = upload.OrderedScanClientItemId,
    };

    private static async Task CopyExactAsync(
        Stream source,
        Stream destination,
        long expectedBytes,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            long copied = 0;
            while (copied < expectedBytes)
            {
                var requested = (int)Math.Min(buffer.Length, expectedBytes - copied);
                var read = await source.ReadAsync(
                    buffer.AsMemory(0, requested),
                    cancellationToken);
                if (read == 0)
                {
                    throw new EndOfStreamException("The upload chunk ended early.");
                }

                await destination.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken);
                copied += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static async Task<bool> HasAdmissionCapacityAsync(
        long uploadBytes,
        string purpose,
        OokiGraderDbContext db,
        IConfiguration configuration,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        try
        {
            var conservativeExpansion = checked(uploadBytes * 4);
            var projectedPhysicalBytes = checked(
                uploadBytes + conservativeExpansion);
            var root = ResolveIncomingRoot(configuration, environment);
            Directory.CreateDirectory(root);
            var drive = new DriveInfo(Path.GetPathRoot(root)!);
            var reserve = configuration.GetValue(
                "Storage:PhysicalReserveBytes",
                5L * 1024 * 1024 * 1024);
            if (drive.AvailableFreeSpace - reserve < projectedPhysicalBytes)
            {
                return false;
            }

            if (purpose is not ("completed_test" or "completed_test_page"))
            {
                return true;
            }

            var settings = await db.SiteSettings
                .AsNoTracking()
                .SingleAsync(cancellationToken);
            var hardLimit = settings.ManagedScanHardLimitBytes > 0
                ? settings.ManagedScanHardLimitBytes
                : configuration.GetValue(
                    "Storage:HardLimitBytes",
                    150L * 1024 * 1024 * 1024);
            var managedBytes = await db.FileObjects
                .AsNoTracking()
                .Where(file => file.ManagedScanBytes
                    && file.State != "deleted"
                    && file.State != "missing")
                .SumAsync(file => (long?)file.Bytes, cancellationToken) ?? 0;
            var pendingUploadBytes = await db.UploadSessions
                .AsNoTracking()
                .Where(upload => (upload.Purpose == "completed_test"
                        || upload.Purpose == "completed_test_page")
                    && (upload.State == "uploading"
                        || upload.State == "finalizing"
                        || upload.State == "duplicate_pending"))
                .SumAsync(
                    upload => (long?)upload.ExpectedBytes,
                    cancellationToken) ?? 0;
            var pendingWithExpansion = checked(pendingUploadBytes * 5);
            return checked(
                    managedBytes
                    + pendingWithExpansion
                    + projectedPhysicalBytes)
                <= hardLimit;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or OverflowException)
        {
            return false;
        }
    }

    private static string ResolveIncomingRoot(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var configured = configuration["Data:Incoming"] ?? ".data/incoming";
        return Path.IsPathFullyQualified(configured)
            ? Path.GetFullPath(configured)
            : Path.GetFullPath(configured, environment.ContentRootPath);
    }

    private static string ResolveIncomingPath(string root, string relativePath)
    {
        if (relativePath.Length == 0
            || relativePath.IndexOfAny(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new InvalidOperationException("Unsafe incoming path.");
        }

        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var path = Path.GetFullPath(Path.Combine(canonicalRoot, relativePath));
        if (!string.Equals(
            Path.GetDirectoryName(path),
            canonicalRoot,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Incoming path escaped its root.");
        }

        return path;
    }

    private static string? SafeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 500)
        {
            return null;
        }

        var trimmed = fileName.Trim();
        if (trimmed != Path.GetFileName(trimmed)
            || trimmed.Any(character => char.IsControl(character)))
        {
            return null;
        }

        return trimmed;
    }

    private static bool AllowedDeclaredMime(string value) =>
        value is "application/pdf"
            or "image/jpeg"
            or "image/png"
            or "image/tiff"
            or "application/octet-stream";

    private static bool IsSafeClientItemId(string? value) =>
        value is { Length: > 0 and <= 128 }
        && !string.IsNullOrWhiteSpace(value)
        && value.All(character => !char.IsControl(character));

    private static bool MatchesOrderedPageRetry(
        UploadSessionEntity existing,
        CreateUploadRequest request,
        string safeFileName) =>
        string.Equals(
            existing.TestSessionId,
            request.TestSessionId,
            StringComparison.Ordinal)
        && string.Equals(
            existing.OrderedScanBatchId,
            request.OrderedScanBatchId,
            StringComparison.Ordinal)
        && existing.OrderedScanInputOrdinal == request.InputOrdinal
        && string.Equals(
            existing.OrderedScanClientItemId,
            request.ClientItemId?.Trim(),
            StringComparison.Ordinal)
        && string.Equals(
            existing.OriginalFileName,
            safeFileName,
            StringComparison.Ordinal)
        && string.Equals(
            existing.DeclaredMimeType,
            request.DeclaredMimeType.ToLowerInvariant(),
            StringComparison.Ordinal)
        && existing.ExpectedBytes == request.Length
        && string.Equals(
            existing.ExpectedSha256,
            request.ExpectedSha256?.ToLowerInvariant(),
            StringComparison.Ordinal);

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    private sealed record ResolveDuplicateRequest(string Action);
}
