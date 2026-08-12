using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OokiGrader.Application.Identifiers;
using OokiGrader.Domain.Grading;
using OokiGrader.Domain.Templates;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Infrastructure.Tests;

public sealed class OrderedScanPersistenceTests
{
    [Fact]
    public async Task FourPageBatchPersistsOrderedItemsAndAppendOnlySubmissionLineage()
    {
        await using var database = await TestDatabase.CreateAsync();
        var now = database.Clock.UtcNow;
        var staffId = UlidId.New(now);
        var templateId = UlidId.New(now.AddMilliseconds(1));
        var versionId = UlidId.New(now.AddMilliseconds(2));
        var sessionId = UlidId.New(now.AddMilliseconds(3));
        var batchId = UlidId.New(now.AddMilliseconds(4));
        var submissionId = UlidId.New(now.AddMilliseconds(5));

        await using (var seed = database.Factory.CreateDbContext())
        {
            seed.AddRange(
                Staff(staffId, now),
                new TestTemplateEntity
                {
                    Id = templateId,
                    Title = "四ページ理科テスト",
                    Subject = "理科",
                    State = "draft",
                    CreatedByStaffUserId = staffId,
                    CreatedAt = now,
                    UpdatedAt = now,
                },
                new TemplateVersionEntity
                {
                    Id = versionId,
                    TestTemplateId = templateId,
                    VersionNumber = 1,
                    State = "published",
                    DefaultPointsMilli = 1_000,
                    PipelineVersion = "local-v1",
                    TestType = TestType.Other,
                    ExpectedSubmissionPageCount = 4,
                    PublishedByStaffUserId = staffId,
                    PublishedAt = now,
                    ContentHash = new string('a', 64),
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            await seed.SaveChangesAsync();

            seed.TestSessions.Add(new TestSessionEntity
            {
                Id = sessionId,
                TemplateVersionId = versionId,
                TestDate = new DateOnly(2026, 8, 10),
                Priority = "economy",
                State = "open",
                CreatedByStaffUserId = staffId,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await seed.SaveChangesAsync();
        }

        var itemIds = new string[4];
        await using (var context = database.Factory.CreateDbContext())
        {
            context.OrderedScanBatches.Add(new OrderedScanBatchEntity
            {
                Id = batchId,
                TestSessionId = sessionId,
                ExpectedPageCount = 4,
                Status = OrderedScanBatchStatus.Processing,
                AssemblyPolicyVersion = OrderedScanAssemblyPlanner.CurrentPolicyVersion,
                PlanHash = new string('b', 64),
                CreatedByStaffUserId = staffId,
                ExpiresAt = now.AddHours(24),
                CreatedAt = now,
                UpdatedAt = now,
            });
            for (var page = 1; page <= 4; page++)
            {
                var itemId = UlidId.New(now.AddMilliseconds(50 + page));
                itemIds[page - 1] = itemId;
                context.OrderedScanItems.Add(new OrderedScanItemEntity
                {
                    Id = itemId,
                    BatchId = batchId,
                    InputOrdinal = page,
                    ClientItemId = $"client-{page}",
                    OriginalFileName = $"scan-{page:000}.pdf",
                    Status = OrderedScanItemStatus.Pending,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }

            await context.SaveChangesAsync();
        }

        await using (var context = database.Factory.CreateDbContext())
        {
            context.Submissions.Add(new SubmissionEntity
            {
                Id = submissionId,
                TestSessionId = sessionId,
                State = "preprocessing_queued",
                ScanPayloadState = "scan_available",
                AssignmentMethod = "none",
                AttemptNumber = 1,
                UploadedByStaffUserId = staffId,
                OriginalFileName = "ordered-scan-group-1.pdf",
                OrderedScanBatchId = batchId,
                OrderedScanGroupOrdinal = 1,
                AssemblyManifestHash = new string('c', 64),
                UploadCompletedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });

            for (var page = 1; page <= 4; page++)
            {
                var suffix = (char)('d' + page - 1);
                var sha256 = new string(suffix, 64);
                var fileObjectId = UlidId.New(now.AddMilliseconds(10 + page));
                var stagedReferenceId = UlidId.New(now.AddMilliseconds(20 + page));
                var uploadId = UlidId.New(now.AddMilliseconds(40 + page));
                var itemId = itemIds[page - 1];

                context.FileObjects.Add(new FileObjectEntity
                {
                    Id = fileObjectId,
                    Sha256 = sha256,
                    Bytes = 128,
                    VerifiedMime = "application/pdf",
                    Extension = "pdf",
                    RelativeObjectPath = $"ordered/{suffix}/{sha256}.pdf",
                    StorageClass = "managed_scan_original",
                    RetentionClass = "scan_three_months",
                    ManagedScanBytes = true,
                    State = "available",
                    CreatedAt = now,
                    VerifiedAt = now,
                    ReferenceCountCache = 1,
                });
                context.FileReferences.Add(new FileReferenceEntity
                {
                    Id = stagedReferenceId,
                    FileObjectId = fileObjectId,
                    OwnerType = "submission",
                    OwnerId = submissionId,
                    Purpose = "original_scan_page",
                    RetentionAnchorAt = now,
                    CreatedAt = now,
                });
                context.UploadSessions.Add(new UploadSessionEntity
                {
                    Id = uploadId,
                    CreatedByStaffUserId = staffId,
                    Purpose = "ordered_scan_page",
                    DestinationType = "ordered_scan_batch",
                    DestinationId = batchId,
                    OriginalFileName = $"scan-{page:000}.pdf",
                    DeclaredMimeType = "application/pdf",
                    ExpectedBytes = 128,
                    CurrentBytes = 128,
                    FinalSha256 = sha256,
                    IncomingRelativePath = $"incoming/{uploadId}.part",
                    State = "completed",
                    ExpiresAt = now.AddHours(24),
                    OrderedScanBatchId = batchId,
                    OrderedScanInputOrdinal = page,
                    OrderedScanClientItemId = $"client-{page}",
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                var item = await context.OrderedScanItems.SingleAsync(
                    entity => entity.Id == itemId);
                item.UploadSessionId = uploadId;
                item.SourceFileReferenceId = stagedReferenceId;
                item.SourceSha256 = sha256;
                item.SourceBytes = 128;
                item.UploadCompletedAt = now;
                item.DetectedTemplatePageNumber = page;
                item.ClassificationConfidenceBasisPoints = 10_000;
                item.Status = OrderedScanItemStatus.Grouped;
                item.GroupOrdinal = 1;
                item.SubmissionId = submissionId;
                item.SubmissionPageNumber = page;
                context.SubmissionSourcePages.Add(new SubmissionSourcePageEntity
                {
                    Id = UlidId.New(now.AddMilliseconds(60 + page)),
                    SubmissionId = submissionId,
                    PageNumber = page,
                    OrderedScanItemId = itemId,
                    UploadSessionId = uploadId,
                    FileReferenceId = stagedReferenceId,
                    SourcePageNumber = 1,
                    SourceSha256 = sha256,
                    AssemblyPolicyVersion =
                        OrderedScanAssemblyPlanner.CurrentPolicyVersion,
                    CreatedAt = now,
                });
            }

            await context.SaveChangesAsync();
        }

        await using (var verify = database.Factory.CreateDbContext())
        {
            var batch = await verify.OrderedScanBatches
                .AsNoTracking()
                .Include(entity => entity.Items)
                .SingleAsync(entity => entity.Id == batchId);
            var lineage = await verify.SubmissionSourcePages
                .AsNoTracking()
                .OrderBy(entity => entity.PageNumber)
                .ToArrayAsync();

            Assert.Equal(OrderedScanBatchStatus.Processing, batch.Status);
            Assert.Equal(4, batch.ExpectedPageCount);
            Assert.Equal([1, 2, 3, 4], batch.Items
                .OrderBy(entity => entity.InputOrdinal)
                .Select(entity => entity.SubmissionPageNumber!.Value));
            Assert.Equal(4, lineage.Length);
            Assert.Equal(itemIds, lineage.Select(entity => entity.OrderedScanItemId));
            Assert.All(lineage, source => Assert.Equal(1, source.SourcePageNumber));

            await verify.Database.OpenConnectionAsync();
            Assert.Equal(
                "processing",
                (await ScalarAsync(
                    verify,
                    "SELECT status FROM ordered_scan_batch LIMIT 1;"))?.ToString());
            Assert.Equal(
                "grouped",
                (await ScalarAsync(
                    verify,
                    "SELECT status FROM ordered_scan_item LIMIT 1;"))?.ToString());
        }

        await using (var appendOnly = database.Factory.CreateDbContext())
        {
            var source = await appendOnly.SubmissionSourcePages
                .OrderBy(entity => entity.PageNumber)
                .FirstAsync();
            var item = await appendOnly.OrderedScanItems.SingleAsync(
                entity => entity.Id == source.OrderedScanItemId);
            var referenceIds = new[]
            {
                item.SourceFileReferenceId!,
                source.FileReferenceId!,
            };
            item.SourceFileReferenceId = null;
            source.FileReferenceId = null;
            appendOnly.FileReferences.RemoveRange(
                await appendOnly.FileReferences
                    .Where(reference => referenceIds.Contains(reference.Id))
                    .ToArrayAsync());
            await appendOnly.SaveChangesAsync();
        }

        await using (var retained = database.Factory.CreateDbContext())
        {
            var source = await retained.SubmissionSourcePages
                .AsNoTracking()
                .OrderBy(entity => entity.PageNumber)
                .FirstAsync();
            var item = await retained.OrderedScanItems
                .AsNoTracking()
                .SingleAsync(entity => entity.Id == source.OrderedScanItemId);
            Assert.Null(source.FileReferenceId);
            Assert.Null(item.SourceFileReferenceId);
            Assert.Equal(new string('d', 64), source.SourceSha256);
            Assert.Equal(new string('d', 64), item.SourceSha256);
        }

        await using (var appendOnly = database.Factory.CreateDbContext())
        {
            var source = await appendOnly.SubmissionSourcePages
                .OrderBy(entity => entity.PageNumber)
                .FirstAsync();
            source.AssemblyPolicyVersion = "mutated";
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => appendOnly.SaveChangesAsync());
        }

        await using (var directTamper = database.Factory.CreateDbContext())
        {
            await Assert.ThrowsAsync<SqliteException>(() =>
                directTamper.Database.ExecuteSqlRawAsync(
                    "UPDATE submission_source_page " +
                    "SET source_sha256='tampered' WHERE page_number=1;"));
        }

        await using (var duplicate = database.Factory.CreateDbContext())
        {
            duplicate.Submissions.Add(new SubmissionEntity
            {
                Id = UlidId.New(now.AddMinutes(1)),
                TestSessionId = sessionId,
                State = "uploading",
                ScanPayloadState = "scan_available",
                AssignmentMethod = "none",
                AttemptNumber = 1,
                UploadedByStaffUserId = staffId,
                OrderedScanBatchId = batchId,
                OrderedScanGroupOrdinal = 1,
                AssemblyManifestHash = new string('z', 64),
                CreatedAt = now,
                UpdatedAt = now,
            });
            await Assert.ThrowsAsync<DbUpdateException>(
                () => duplicate.SaveChangesAsync());
        }

        var racedObjectId = UlidId.New(now.AddMinutes(2));
        var racedReferenceId = UlidId.New(now.AddMinutes(2).AddMilliseconds(1));
        await using (var finalizer = database.Factory.CreateDbContext())
        await using (var canceller = database.Factory.CreateDbContext())
        {
            var finalizerBatch = await finalizer.OrderedScanBatches
                .SingleAsync(entity => entity.Id == batchId);
            var cancellerBatch = await canceller.OrderedScanBatches
                .SingleAsync(entity => entity.Id == batchId);

            finalizerBatch.UpdatedAt = now.AddMinutes(2);
            finalizer.FileObjects.Add(new FileObjectEntity
            {
                Id = racedObjectId,
                Sha256 = new string('h', 64),
                Bytes = 128,
                VerifiedMime = "application/pdf",
                Extension = "pdf",
                RelativeObjectPath = $"ordered/h/{new string('h', 64)}.pdf",
                StorageClass = "managed_scan_original",
                RetentionClass = "scan_three_months",
                ManagedScanBytes = true,
                State = "available",
                CreatedAt = now,
                VerifiedAt = now,
                ReferenceCountCache = 1,
            });
            finalizer.FileReferences.Add(new FileReferenceEntity
            {
                Id = racedReferenceId,
                FileObjectId = racedObjectId,
                OwnerType = "ordered_scan_batch",
                OwnerId = batchId,
                Purpose = "ordered_scan_page",
                RetentionAnchorAt = now,
                CreatedAt = now,
            });

            cancellerBatch.Status = OrderedScanBatchStatus.Cancelled;
            cancellerBatch.UpdatedAt = now.AddMinutes(1);
            await canceller.SaveChangesAsync();

            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
                () => finalizer.SaveChangesAsync());
        }

        await using (var verifyFence = database.Factory.CreateDbContext())
        {
            Assert.Equal(
                OrderedScanBatchStatus.Cancelled,
                (await verifyFence.OrderedScanBatches
                    .AsNoTracking()
                    .SingleAsync(entity => entity.Id == batchId))
                .Status);
            Assert.False(await verifyFence.FileObjects
                .AnyAsync(entity => entity.Id == racedObjectId));
            Assert.False(await verifyFence.FileReferences
                .AnyAsync(entity => entity.Id == racedReferenceId));
        }
    }

    [Fact]
    public async Task Migration0018RoundTripPreservesTablesTriggersAndHistoricalRows()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "ooki-grader-ordered-scan-migration-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);

        try
        {
            var options = Options(Path.Combine(rootPath, "upgrade.db"));
            var clock = new TestClock(new DateTimeOffset(
                2026,
                8,
                10,
                3,
                0,
                0,
                TimeSpan.Zero));
            await using var context = new OokiGraderDbContext(options, clock);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(
                "20260809112529_0017_DeterministicTemplateGenerationBatches");

            foreach (var statement in
                     TemplateVersionIntegrityTriggerCatalog.Schema17Statements)
            {
                await context.Database.ExecuteSqlRawAsync(statement);
            }

            await context.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER trg_0018_upload_session_sentinel
                AFTER UPDATE ON upload_session BEGIN SELECT 1; END;
                CREATE TRIGGER trg_0018_submission_sentinel
                AFTER UPDATE ON submission BEGIN SELECT 1; END;
                """);
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO test_template (
                    id, title, state, created_by_staff_user_id,
                    created_at, updated_at, revision, default_points_milli)
                VALUES (
                    '01K13ORDEREDTEMPLATE0001', 'Ordered migration', 'draft',
                    '01K13ORDEREDSTAFF000001', 1, 1, 1, 1000);

                INSERT INTO template_version (
                    id, test_template_id, version_number, state,
                    default_allow_non_kanji, pipeline_version,
                    published_by_staff_user_id, published_at, content_hash,
                    created_at, updated_at, revision, default_points_milli)
                VALUES (
                    '01K13ORDEREDVERSION00001', '01K13ORDEREDTEMPLATE0001',
                    1, 'published', 0, 'test', '01K13ORDEREDSTAFF000001',
                    1, 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                    1, 1, 1, 1000);

                INSERT INTO template_version (
                    id, test_template_id, version_number, state,
                    default_allow_non_kanji, pipeline_version,
                    created_at, updated_at, revision, default_points_milli)
                VALUES (
                    '01K13ORDEREDVERSION00002', '01K13ORDEREDTEMPLATE0001',
                    2, 'draft', 0, 'test', 1, 1, 1, 1000);
                """);
            await context.Database.OpenConnectionAsync();
            var templateRootPage = await ScalarAsync(
                context,
                "SELECT rootpage FROM sqlite_master " +
                "WHERE type='table' AND name='template_version';");
            var submissionRootPage = await ScalarAsync(
                context,
                "SELECT rootpage FROM sqlite_master " +
                "WHERE type='table' AND name='submission';");
            var uploadRootPage = await ScalarAsync(
                context,
                "SELECT rootpage FROM sqlite_master " +
                "WHERE type='table' AND name='upload_session';");
            await context.Database.CloseConnectionAsync();

            await migrator.MigrateAsync();
            await context.Database.OpenConnectionAsync();

            Assert.Equal(
                templateRootPage,
                await ScalarAsync(
                    context,
                    "SELECT rootpage FROM sqlite_master " +
                    "WHERE type='table' AND name='template_version';"));
            Assert.Equal(
                submissionRootPage,
                await ScalarAsync(
                    context,
                    "SELECT rootpage FROM sqlite_master " +
                    "WHERE type='table' AND name='submission';"));
            Assert.Equal(
                uploadRootPage,
                await ScalarAsync(
                    context,
                    "SELECT rootpage FROM sqlite_master " +
                    "WHERE type='table' AND name='upload_session';"));
            Assert.Equal(
                2L,
                Convert.ToInt64(
                    await ScalarAsync(
                        context,
                        "SELECT COUNT(*) FROM sqlite_master " +
                        "WHERE type='trigger' AND name IN " +
                        "('trg_0018_upload_session_sentinel'," +
                        "'trg_0018_submission_sentinel');"),
                    CultureInfo.InvariantCulture));
            Assert.Equal(
                1L,
                Convert.ToInt64(
                    await ScalarAsync(
                        context,
                        "SELECT COUNT(*) FROM sqlite_master " +
                        "WHERE type='table' AND name='ordered_scan_batch';"),
                    CultureInfo.InvariantCulture));
            Assert.Equal(
                DBNull.Value,
                await ScalarAsync(
                    context,
                    "SELECT expected_submission_page_count " +
                    "FROM template_version WHERE version_number=1;"));

            await context.Database.CloseConnectionAsync();
            await Assert.ThrowsAsync<SqliteException>(() =>
                context.Database.ExecuteSqlRawAsync(
                    "UPDATE template_version " +
                    "SET expected_submission_page_count=0 WHERE version_number=2;"));
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE template_version " +
                "SET expected_submission_page_count=4 WHERE version_number=2;");
            await Assert.ThrowsAsync<SqliteException>(() =>
                context.Database.ExecuteSqlRawAsync(
                    "UPDATE template_version " +
                    "SET expected_submission_page_count=2 WHERE version_number=1;"));

            await migrator.MigrateAsync(
                "20260809112529_0017_DeterministicTemplateGenerationBatches");
            await context.Database.OpenConnectionAsync();
            Assert.Equal(
                0L,
                Convert.ToInt64(
                    await ScalarAsync(
                        context,
                        "SELECT COUNT(*) FROM pragma_table_info('template_version') " +
                        "WHERE name='expected_submission_page_count';"),
                    CultureInfo.InvariantCulture));
            Assert.Equal(
                0L,
                Convert.ToInt64(
                    await ScalarAsync(
                        context,
                        "SELECT COUNT(*) FROM sqlite_master " +
                        "WHERE type='table' AND name='ordered_scan_batch';"),
                    CultureInfo.InvariantCulture));
            Assert.Equal(
                2L,
                Convert.ToInt64(
                    await ScalarAsync(
                        context,
                        "SELECT COUNT(*) FROM sqlite_master " +
                        "WHERE type='trigger' AND name IN " +
                        "('trg_0018_upload_session_sentinel'," +
                        "'trg_0018_submission_sentinel');"),
                    CultureInfo.InvariantCulture));

            await context.Database.CloseConnectionAsync();
            await migrator.MigrateAsync();
            await context.Database.OpenConnectionAsync();
            var immutableTriggerSql = await ScalarAsync(
                context,
                "SELECT sql FROM sqlite_master WHERE type='trigger' " +
                "AND name='trg_published_template_version_content_immutable';");
            Assert.Contains(
                "expected_submission_page_count",
                immutableTriggerSql?.ToString(),
                StringComparison.Ordinal);
            Assert.Equal(
                "ok",
                (await ScalarAsync(context, "PRAGMA integrity_check;"))?.ToString(),
                ignoreCase: true);
            Assert.Equal(
                0L,
                Convert.ToInt64(
                    await ScalarAsync(
                        context,
                        "SELECT COUNT(*) FROM pragma_foreign_key_check;"),
                    CultureInfo.InvariantCulture));
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private static StaffUserEntity Staff(string id, DateTimeOffset now) =>
        new()
        {
            Id = id,
            Username = "ordered.scan.teacher",
            UsernameNormalized = "ordered.scan.teacher",
            DisplayName = "採点担当",
            PasswordHash = "argon2id:test",
            PasswordAlgorithm = "argon2id",
            PasswordAlgorithmVersion = 1,
            Status = "active",
            CredentialChangedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

    private static DbContextOptions<OokiGraderDbContext> Options(
        string databasePath) =>
        new DbContextOptionsBuilder<OokiGraderDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                ForeignKeys = true,
                DefaultTimeout = 5,
                Pooling = false,
            }.ToString())
            .AddInterceptors(new SqlitePragmaConnectionInterceptor())
            .Options;

    private static async Task<object?> ScalarAsync(
        OokiGraderDbContext context,
        string sql)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
