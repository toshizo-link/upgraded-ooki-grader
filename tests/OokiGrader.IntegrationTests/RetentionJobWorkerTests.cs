using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Domain.Grading;
using OokiGrader.Domain.Templates;
using OokiGrader.Host.Jobs;
using OokiGrader.Host.Uploads;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Infrastructure.Storage;

namespace OokiGrader.IntegrationTests;

public sealed class RetentionJobWorkerTests
{
    [Fact]
    public async Task DailyScheduleEnqueuesOneRetentionJobPerSiteDate()
    {
        await using var fixture = await RetentionFixture.CreateAsync();
        var worker = fixture.CreateWorker();

        await worker.EnsureScheduledJobAsync();
        await worker.EnsureScheduledJobAsync();

        await using var db = fixture.Factory.CreateDbContext();
        var job = await db.BackgroundJobs.AsNoTracking().SingleAsync();
        Assert.Equal(RetentionJobWorker.JobType, job.Type);
        Assert.Equal("retention:scheduled:20260727", job.DeduplicationKey);
        Assert.Equal("queued", job.State);
        Assert.Contains("\"reason\":\"scheduled\"", job.PayloadJson);
    }

    [Fact]
    public async Task AgeCleanupDeletesOnlyScanPayloadAndLeavesOtherJobTypesAlone()
    {
        await using var fixture = await RetentionFixture.CreateAsync();
        var seeded = await fixture.SeedSubmissionAsync(
            uploadCompletedAt: fixture.UtcNow.AddMonths(-4),
            includeGrade: true);
        var unrelatedJobId = await fixture.EnqueueJobAsync(
            "submission.preprocess",
            priority: 1_000);

        var processed = await fixture.CreateWorker().ProcessNextAsync();

        Assert.True(processed);
        Assert.False(await fixture.ContentStore.ExistsAsync(seeded.Locator));
        await using var db = fixture.Factory.CreateDbContext();
        var submission = await db.Submissions
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.SubmissionId);
        var fileObject = await db.FileObjects
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.FileObjectId);
        var manifest = await db.DeletionManifests
            .AsNoTracking()
            .Include(item => item.Items)
            .SingleAsync();
        var retentionJob = await db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.RetentionJobId);
        var unrelatedJob = await db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Id == unrelatedJobId);

        Assert.Equal("scan_deleted", submission.ScanPayloadState);
        Assert.Equal(fixture.UtcNow, submission.ScanDeletedAt);
        Assert.Equal("age", submission.ScanDeletionReason);
        Assert.Null(submission.OriginalFileObjectId);
        Assert.Equal("finalized", submission.State);
        Assert.Equal("deleted", fileObject.State);
        Assert.NotNull(fileObject.DeletedAt);
        Assert.Equal(0, fileObject.ReferenceCountCache);
        Assert.Empty(await db.FileReferences.AsNoTracking().ToListAsync());
        Assert.Equal("completed", manifest.State);
        Assert.Equal("age", manifest.Reason);
        Assert.Equal(1, manifest.DeletedObjectCount);
        Assert.Equal(seeded.Locator.Bytes, manifest.DeletedBytes);
        Assert.Equal("deleted", Assert.Single(manifest.Items).State);
        Assert.Equal("succeeded", retentionJob.State);
        Assert.Equal("queued", unrelatedJob.State);
        Assert.Equal(
            3_500,
            await db.GradingRuns
                .AsNoTracking()
                .Where(item => item.SubmissionId == seeded.SubmissionId)
                .Select(item => item.EarnedPointsMilli)
                .SingleAsync());
    }

    [Fact]
    public async Task AgeCleanupDeletesOrderedScanSourcesAndCompositeWhileRetainingLineage()
    {
        await using var fixture = await RetentionFixture.CreateAsync();
        var seeded = await fixture.SeedOrderedSubmissionAsync(
            fixture.UtcNow.AddMonths(-4));
        var interruptedObjectId = seeded.FileObjectIds[^1];
        await using (var partialState = fixture.Factory.CreateDbContext())
        {
            var interruptedObject = await partialState.FileObjects
                .SingleAsync(item => item.Id == interruptedObjectId);
            interruptedObject.State = "deletion_pending";
            await partialState.SaveChangesAsync();
        }

        var processed = await fixture.CreateWorker().ProcessNextAsync();

        Assert.True(processed);
        foreach (var locator in seeded.Locators)
        {
            Assert.False(await fixture.ContentStore.ExistsAsync(locator));
        }

        await using var db = fixture.Factory.CreateDbContext();
        var submission = await db.Submissions
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.SubmissionId);
        var fileObjects = await db.FileObjects
            .AsNoTracking()
            .Where(item => seeded.FileObjectIds.Contains(item.Id))
            .ToArrayAsync();
        var remainingReferenceIds = await db.FileReferences
            .AsNoTracking()
            .Where(item => seeded.FileReferenceIds.Contains(item.Id))
            .Select(item => item.Id)
            .ToArrayAsync();
        var sourcePages = await db.SubmissionSourcePages
            .AsNoTracking()
            .Where(item => item.SubmissionId == seeded.SubmissionId)
            .OrderBy(item => item.PageNumber)
            .ToArrayAsync();
        var orderedItems = await db.OrderedScanItems
            .AsNoTracking()
            .Where(item => item.SubmissionId == seeded.SubmissionId)
            .OrderBy(item => item.InputOrdinal)
            .ToArrayAsync();
        var retainedRasterPages = await db.SubmissionPages
            .AsNoTracking()
            .Where(item => item.SubmissionId == seeded.SubmissionId)
            .ToArrayAsync();
        var gradingRun = await db.GradingRuns
            .AsNoTracking()
            .SingleAsync(item => item.SubmissionId == seeded.SubmissionId);
        var manifest = await db.DeletionManifests
            .AsNoTracking()
            .Include(item => item.Items)
            .SingleAsync();

        Assert.Equal("scan_deleted", submission.ScanPayloadState);
        Assert.Equal(fixture.UtcNow, submission.ScanDeletedAt);
        Assert.Equal("age", submission.ScanDeletionReason);
        Assert.Null(submission.OriginalFileObjectId);
        Assert.Empty(remainingReferenceIds);
        Assert.Empty(retainedRasterPages);
        Assert.Equal("finalized", gradingRun.State);
        Assert.Equal(3_500, gradingRun.EarnedPointsMilli);
        Assert.Equal(5_000, gradingRun.PossiblePointsMilli);
        Assert.Equal(7, fileObjects.Length);
        Assert.All(fileObjects, fileObject =>
        {
            Assert.Equal("deleted", fileObject.State);
            Assert.Equal(fixture.UtcNow, fileObject.DeletedAt);
            Assert.Equal(0, fileObject.ReferenceCountCache);
        });

        Assert.Equal([1, 2], sourcePages.Select(item => item.PageNumber));
        Assert.Equal(seeded.ItemIds, sourcePages.Select(item => item.OrderedScanItemId));
        Assert.Equal(seeded.SourceSha256s, sourcePages.Select(item => item.SourceSha256));
        Assert.All(sourcePages, item => Assert.Null(item.FileReferenceId));
        Assert.All(sourcePages, item => Assert.Equal(1, item.SourcePageNumber));

        Assert.Equal([1, 2], orderedItems.Select(item => item.InputOrdinal));
        Assert.Equal([1, 2], orderedItems.Select(item => item.SubmissionPageNumber));
        Assert.Equal(seeded.SourceSha256s, orderedItems.Select(item => item.SourceSha256));
        Assert.All(orderedItems, item =>
        {
            Assert.Null(item.SourceFileReferenceId);
            Assert.Equal(OrderedScanItemStatus.Grouped, item.Status);
            Assert.Equal(1, item.GroupOrdinal);
            Assert.Equal(seeded.SubmissionId, item.SubmissionId);
            Assert.NotNull(item.SourceBytes);
            Assert.NotNull(item.UploadCompletedAt);
        });

        Assert.Equal("completed", manifest.State);
        Assert.Equal(7, manifest.PlannedObjectCount);
        Assert.Equal(7, manifest.PlannedReferenceCount);
        Assert.Equal(7, manifest.DeletedObjectCount);
        Assert.Equal(7, manifest.ReleasedReferenceCount);

        await db.Database.OpenConnectionAsync();
        await using (var foreignKeyCheck = db.Database
                         .GetDbConnection()
                         .CreateCommand())
        {
            foreignKeyCheck.CommandText = "PRAGMA foreign_key_check;";
            await using var reader = await foreignKeyCheck.ExecuteReaderAsync();
            Assert.False(await reader.ReadAsync());
        }

        await using (var integrityCheck = db.Database
                         .GetDbConnection()
                         .CreateCommand())
        {
            integrityCheck.CommandText = "PRAGMA integrity_check;";
            Assert.Equal("ok", await integrityCheck.ExecuteScalarAsync());
        }
    }

    [Fact]
    public async Task FilesystemFailureRestoresAvailableStateAndKeepsReferences()
    {
        await using var fixture = await RetentionFixture.CreateAsync();
        var seeded = await fixture.SeedSubmissionAsync(
            uploadCompletedAt: fixture.UtcNow.AddMonths(-4),
            includeGrade: true);
        var worker = fixture.CreateWorker(
            new DeleteFailingContentStore(fixture.ContentStore));

        var processed = await worker.ProcessNextAsync();

        Assert.True(processed);
        Assert.True(await fixture.ContentStore.ExistsAsync(seeded.Locator));
        await using var db = fixture.Factory.CreateDbContext();
        var submission = await db.Submissions
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.SubmissionId);
        var fileObject = await db.FileObjects
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.FileObjectId);
        var manifest = await db.DeletionManifests
            .AsNoTracking()
            .Include(item => item.Items)
            .SingleAsync();
        var job = await db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.RetentionJobId);

        Assert.Equal("scan_available", submission.ScanPayloadState);
        Assert.Null(submission.ScanDeletedAt);
        Assert.Null(submission.ScanDeletionReason);
        Assert.Equal(seeded.FileObjectId, submission.OriginalFileObjectId);
        Assert.Equal("available", fileObject.State);
        Assert.Equal(1, fileObject.ReferenceCountCache);
        Assert.Single(await db.FileReferences.AsNoTracking().ToListAsync());
        Assert.Equal("failed", manifest.State);
        Assert.Equal("failed", Assert.Single(manifest.Items).State);
        Assert.Equal("retry_waiting", job.State);
        Assert.Equal("retention_file_delete_failed", job.ErrorCode);
        Assert.Equal(
            1,
            await db.GradingRuns
                .AsNoTracking()
                .CountAsync(item => item.SubmissionId == seeded.SubmissionId));
    }

    [Fact]
    public async Task SharedObjectRemainsWhenARecentSubmissionStillReferencesIt()
    {
        await using var fixture = await RetentionFixture.CreateAsync();
        var seeded = await fixture.SeedSubmissionAsync(
            uploadCompletedAt: fixture.UtcNow.AddMonths(-4),
            includeGrade: false,
            sharedRecentSubmission: true);

        await fixture.CreateWorker().ProcessNextAsync();

        Assert.True(await fixture.ContentStore.ExistsAsync(seeded.Locator));
        await using var db = fixture.Factory.CreateDbContext();
        var oldSubmission = await db.Submissions
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.SubmissionId);
        var recentSubmission = await db.Submissions
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.RecentSubmissionId);
        var fileObject = await db.FileObjects
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.FileObjectId);
        var item = await db.DeletionManifestItems.AsNoTracking().SingleAsync();

        Assert.Equal("scan_deleted", oldSubmission.ScanPayloadState);
        Assert.Equal(fixture.UtcNow, oldSubmission.ScanDeletedAt);
        Assert.Equal("age", oldSubmission.ScanDeletionReason);
        Assert.Null(oldSubmission.OriginalFileObjectId);
        Assert.Equal("scan_available", recentSubmission.ScanPayloadState);
        Assert.Equal(seeded.FileObjectId, recentSubmission.OriginalFileObjectId);
        Assert.Equal("available", fileObject.State);
        Assert.Equal(1, fileObject.ReferenceCountCache);
        Assert.Equal("reference_released", item.State);
        Assert.False(item.DeletePhysicalObject);
        Assert.Single(await db.FileReferences.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task QuotaCleanupCanRemoveARecentScanDownToTheLowWaterMark()
    {
        await using var fixture = await RetentionFixture.CreateAsync();
        var seeded = await fixture.SeedSubmissionAsync(
            uploadCompletedAt: fixture.UtcNow.AddDays(-1),
            includeGrade: false);
        await using (var settingsDb = fixture.Factory.CreateDbContext())
        {
            var settings = await settingsDb.SiteSettings.SingleAsync();
            settings.ManagedScanWarningBytes = 1;
            settings.ManagedScanCleanupTargetBytes = seeded.Locator.Bytes - 1;
            settings.ManagedScanHardLimitBytes = seeded.Locator.Bytes + 1;
            await settingsDb.SaveChangesAsync();
        }

        await fixture.CreateWorker().ProcessNextAsync();

        await using var db = fixture.Factory.CreateDbContext();
        var manifest = await db.DeletionManifests.AsNoTracking().SingleAsync();
        var submission = await db.Submissions
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.SubmissionId);
        Assert.Equal("quota", manifest.Reason);
        Assert.Equal("completed", manifest.State);
        Assert.Equal("scan_deleted", submission.ScanPayloadState);
        Assert.Equal(fixture.UtcNow, submission.ScanDeletedAt);
        Assert.Equal("quota", submission.ScanDeletionReason);
        Assert.False(await fixture.ContentStore.ExistsAsync(seeded.Locator));
    }

    private sealed class RetentionFixture : IAsyncDisposable
    {
        private readonly FixedClock _clock;
        private readonly SemaphoreWriteCoordinator _writeCoordinator;

        private RetentionFixture(
            string rootPath,
            FixedClock clock,
            FixedTimeProvider timeProvider,
            RetentionDbContextFactory factory,
            NtfsContentStore contentStore,
            SemaphoreWriteCoordinator writeCoordinator)
        {
            RootPath = rootPath;
            _clock = clock;
            TimeProvider = timeProvider;
            Factory = factory;
            ContentStore = contentStore;
            _writeCoordinator = writeCoordinator;
        }

        public string RootPath { get; }
        public DateTimeOffset UtcNow => _clock.UtcNow;
        public FixedTimeProvider TimeProvider { get; }
        public RetentionDbContextFactory Factory { get; }
        public NtfsContentStore ContentStore { get; }

        public static async Task<RetentionFixture> CreateAsync()
        {
            var rootPath = Path.Combine(
                Path.GetTempPath(),
                "ooki-retention-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(rootPath, "ooki.db"),
                Mode = SqliteOpenMode.ReadWriteCreate,
                ForeignKeys = true,
                DefaultTimeout = 5,
                Pooling = false,
            }.ToString();
            var options = new DbContextOptionsBuilder<OokiGraderDbContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(new SqlitePragmaConnectionInterceptor())
                .Options;
            var utcNow = new DateTimeOffset(
                2026,
                7,
                27,
                3,
                0,
                0,
                TimeSpan.Zero);
            var clock = new FixedClock(utcNow);
            var factory = new RetentionDbContextFactory(options, clock);
            await using (var db = factory.CreateDbContext())
            {
                var initializer = new OokiDatabaseInitializer(db, clock);
                await initializer.InitializeAsync(
                    new OokiDatabaseInitializationOptions(
                        rootPath,
                        SchoolName: "Retention Test School"));
            }

            var contentStore = new NtfsContentStore(new ContentStoreOptions
            {
                RootPath = Path.Combine(rootPath, "objects"),
            });
            return new RetentionFixture(
                rootPath,
                clock,
                new FixedTimeProvider(utcNow),
                factory,
                contentStore,
                new SemaphoreWriteCoordinator());
        }

        public RetentionJobWorker CreateWorker(IContentStore? store = null)
        {
            return new RetentionJobWorker(
                Factory,
                _writeCoordinator,
                store ?? ContentStore,
                TimeProvider,
                NullLogger<RetentionJobWorker>.Instance,
                new ContentObjectLockProvider());
        }

        public async Task<SeededSubmission> SeedSubmissionAsync(
            DateTimeOffset uploadCompletedAt,
            bool includeGrade,
            bool sharedRecentSubmission = false)
        {
            var payload = Encoding.UTF8.GetBytes(
                $"retention-payload-{Guid.NewGuid():N}");
            await using var source = new MemoryStream(payload);
            var stored = await ContentStore.PutAsync(
                source,
                ContentStorageClass.ManagedScanOriginal,
                "bin");

            var templateId = UlidId.New(UtcNow.AddMilliseconds(1));
            var versionId = UlidId.New(UtcNow.AddMilliseconds(2));
            var sessionId = UlidId.New(UtcNow.AddMilliseconds(3));
            var fileObjectId = UlidId.New(UtcNow.AddMilliseconds(4));
            var submissionId = UlidId.New(UtcNow.AddMilliseconds(5));
            var retentionJobId = UlidId.New(UtcNow.AddMilliseconds(6));
            string? recentSubmissionId = null;

            await using var db = Factory.CreateDbContext();
            db.TestTemplates.Add(new TestTemplateEntity
            {
                Id = templateId,
                Title = "Retention fixture",
                State = "draft",
                CreatedByStaffUserId = UlidId.New(UtcNow),
                CreatedAt = UtcNow,
                UpdatedAt = UtcNow,
            });
            db.TemplateVersions.Add(new TemplateVersionEntity
            {
                Id = versionId,
                TestTemplateId = templateId,
                VersionNumber = 1,
                State = "published",
                TargetTotalPointsMilli = 5_000,
                PipelineVersion = "retention-test-v1",
                PublishedByStaffUserId = UlidId.New(UtcNow),
                PublishedAt = UtcNow,
                ContentHash = new string('a', 64),
                CreatedAt = UtcNow,
                UpdatedAt = UtcNow,
            });
            db.TestSessions.Add(new TestSessionEntity
            {
                Id = sessionId,
                TemplateVersionId = versionId,
                TestDate = DateOnly.FromDateTime(UtcNow.UtcDateTime),
                Priority = "economy",
                State = "closed",
                CreatedByStaffUserId = UlidId.New(UtcNow),
                CreatedAt = UtcNow,
                UpdatedAt = UtcNow,
                ClosedAt = UtcNow,
            });
            db.FileObjects.Add(new FileObjectEntity
            {
                Id = fileObjectId,
                Sha256 = stored.Locator.Sha256,
                Bytes = stored.Locator.Bytes,
                VerifiedMime = "application/octet-stream",
                Extension = stored.Locator.Extension,
                RelativeObjectPath = stored.RelativePath,
                StorageClass = ContentStorageClass.ManagedScanOriginal.ToString(),
                RetentionClass = "submitted_scan",
                ManagedScanBytes = true,
                State = "available",
                CreatedAt = uploadCompletedAt,
                VerifiedAt = uploadCompletedAt,
                ReferenceCountCache = sharedRecentSubmission ? 2 : 1,
            });
            db.Submissions.Add(CreateSubmission(
                submissionId,
                sessionId,
                fileObjectId,
                uploadCompletedAt));
            db.FileReferences.Add(new FileReferenceEntity
            {
                Id = UlidId.New(UtcNow.AddMilliseconds(7)),
                FileObjectId = fileObjectId,
                OwnerType = "submission",
                OwnerId = submissionId,
                Purpose = "original_scan",
                RetentionAnchorAt = uploadCompletedAt,
                CreatedAt = uploadCompletedAt,
            });

            if (sharedRecentSubmission)
            {
                recentSubmissionId = UlidId.New(UtcNow.AddMilliseconds(8));
                db.Submissions.Add(CreateSubmission(
                    recentSubmissionId,
                    sessionId,
                    fileObjectId,
                    UtcNow.AddDays(-1)));
                db.FileReferences.Add(new FileReferenceEntity
                {
                    Id = UlidId.New(UtcNow.AddMilliseconds(9)),
                    FileObjectId = fileObjectId,
                    OwnerType = "submission",
                    OwnerId = recentSubmissionId,
                    Purpose = "original_scan",
                    RetentionAnchorAt = UtcNow.AddDays(-1),
                    CreatedAt = UtcNow.AddDays(-1),
                });
            }

            if (includeGrade)
            {
                db.GradingRuns.Add(new GradingRunEntity
                {
                    Id = UlidId.New(UtcNow.AddMilliseconds(10)),
                    SubmissionId = submissionId,
                    RunNumber = 1,
                    TemplateVersionId = versionId,
                    Reason = "initial",
                    State = "finalized",
                    PipelineVersion = "retention-test-v1",
                    CanonicalInputManifestHash = new string('b', 64),
                    EarnedPointsMilli = 3_500,
                    PossiblePointsMilli = 5_000,
                    CreatedAt = UtcNow,
                    FinishedAt = UtcNow,
                    FinalizedAt = UtcNow,
                });
            }

            db.BackgroundJobs.Add(new BackgroundJobEntity
            {
                Id = retentionJobId,
                Type = RetentionJobWorker.JobType,
                SchemaVersion = 1,
                DeduplicationKey = $"retention:test:{retentionJobId}",
                Priority = 100,
                PayloadJson = """{"reason":"test"}""",
                State = "queued",
                MaxAttempts = 3,
                NextAttemptAt = UtcNow,
                CreatedAt = UtcNow,
                UpdatedAt = UtcNow,
            });
            await db.SaveChangesAsync();

            return new SeededSubmission(
                submissionId,
                recentSubmissionId,
                fileObjectId,
                retentionJobId,
                stored.Locator);
        }

        public async Task<SeededOrderedSubmission> SeedOrderedSubmissionAsync(
            DateTimeOffset uploadCompletedAt)
        {
            var pageWrites = new ContentWriteResult[2];
            for (var pageNumber = 1; pageNumber <= pageWrites.Length; pageNumber++)
            {
                var payload = Encoding.UTF8.GetBytes(
                    $"ordered-source-page-{pageNumber}-{Guid.NewGuid():N}");
                await using var source = new MemoryStream(payload);
                pageWrites[pageNumber - 1] = await ContentStore.PutAsync(
                    source,
                    ContentStorageClass.ManagedScanOriginal,
                    "pdf");
            }

            var compositePayload = Encoding.UTF8.GetBytes(
                $"ordered-composite-{Guid.NewGuid():N}");
            await using var compositeSource = new MemoryStream(compositePayload);
            var compositeWrite = await ContentStore.PutAsync(
                compositeSource,
                ContentStorageClass.ManagedScanOriginal,
                "pdf");
            var normalizedWrites = new ContentWriteResult[pageWrites.Length];
            var thumbnailWrites = new ContentWriteResult[pageWrites.Length];
            for (var pageNumber = 1; pageNumber <= pageWrites.Length; pageNumber++)
            {
                await using var normalizedSource = new MemoryStream(
                    Encoding.UTF8.GetBytes(
                        $"normalized-page-{pageNumber}-{Guid.NewGuid():N}"));
                normalizedWrites[pageNumber - 1] = await ContentStore.PutAsync(
                    normalizedSource,
                    ContentStorageClass.ManagedScanDerived,
                    "png");
                await using var thumbnailSource = new MemoryStream(
                    Encoding.UTF8.GetBytes(
                        $"thumbnail-page-{pageNumber}-{Guid.NewGuid():N}"));
                thumbnailWrites[pageNumber - 1] = await ContentStore.PutAsync(
                    thumbnailSource,
                    ContentStorageClass.ManagedScanDerived,
                    "png");
            }

            var staffId = UlidId.New(UtcNow.AddMilliseconds(30));
            var templateId = UlidId.New(UtcNow.AddMilliseconds(31));
            var versionId = UlidId.New(UtcNow.AddMilliseconds(32));
            var sessionId = UlidId.New(UtcNow.AddMilliseconds(33));
            var batchId = UlidId.New(UtcNow.AddMilliseconds(34));
            var submissionId = UlidId.New(UtcNow.AddMilliseconds(35));
            var retentionJobId = UlidId.New(UtcNow.AddMilliseconds(36));
            var compositeObjectId = UlidId.New(UtcNow.AddMilliseconds(37));
            var compositeReferenceId = UlidId.New(UtcNow.AddMilliseconds(38));
            var itemIds = new string[pageWrites.Length];
            var objectIds = new string[(pageWrites.Length * 3) + 1];
            var referenceIds = new string[(pageWrites.Length * 3) + 1];
            objectIds[0] = compositeObjectId;
            referenceIds[0] = compositeReferenceId;

            await using var db = Factory.CreateDbContext();
            db.StaffUsers.Add(new StaffUserEntity
            {
                Id = staffId,
                Username = "ordered.retention.teacher",
                UsernameNormalized = "ordered.retention.teacher",
                DisplayName = "Ordered retention teacher",
                PasswordHash = "argon2id:test",
                PasswordAlgorithm = "argon2id",
                PasswordAlgorithmVersion = 1,
                Status = "active",
                CredentialChangedAt = UtcNow,
                CreatedAt = UtcNow,
                UpdatedAt = UtcNow,
            });
            db.TestTemplates.Add(new TestTemplateEntity
            {
                Id = templateId,
                Title = "Ordered retention fixture",
                State = "draft",
                CreatedByStaffUserId = staffId,
                CreatedAt = UtcNow,
                UpdatedAt = UtcNow,
            });
            db.TemplateVersions.Add(new TemplateVersionEntity
            {
                Id = versionId,
                TestTemplateId = templateId,
                VersionNumber = 1,
                State = "published",
                TargetTotalPointsMilli = 5_000,
                PipelineVersion = "retention-test-v1",
                TestType = TestType.Step,
                ExpectedSubmissionPageCount = 2,
                PublishedByStaffUserId = staffId,
                PublishedAt = UtcNow,
                ContentHash = new string('a', 64),
                CreatedAt = UtcNow,
                UpdatedAt = UtcNow,
            });
            db.TestSessions.Add(new TestSessionEntity
            {
                Id = sessionId,
                TemplateVersionId = versionId,
                TestDate = DateOnly.FromDateTime(UtcNow.UtcDateTime),
                Priority = "economy",
                State = "closed",
                CreatedByStaffUserId = staffId,
                CreatedAt = UtcNow,
                UpdatedAt = UtcNow,
                ClosedAt = UtcNow,
            });
            db.OrderedScanBatches.Add(new OrderedScanBatchEntity
            {
                Id = batchId,
                TestSessionId = sessionId,
                ExpectedPageCount = 2,
                Status = OrderedScanBatchStatus.Completed,
                AssemblyPolicyVersion =
                    OrderedScanAssemblyPlanner.CurrentPolicyVersion,
                PlanHash = new string('b', 64),
                CreatedByStaffUserId = staffId,
                ExpiresAt = uploadCompletedAt.AddHours(24),
                CreatedAt = uploadCompletedAt,
                UpdatedAt = uploadCompletedAt,
                CompletedAt = uploadCompletedAt,
            });
            db.FileObjects.Add(CreateManagedFileObject(
                compositeObjectId,
                compositeWrite,
                uploadCompletedAt));
            db.Submissions.Add(new SubmissionEntity
            {
                Id = submissionId,
                TestSessionId = sessionId,
                State = "finalized",
                ScanPayloadState = "scan_available",
                AssignmentMethod = "none",
                AttemptNumber = 1,
                UploadedByStaffUserId = staffId,
                OriginalFileName = "ordered-scan-group-1.pdf",
                OriginalFileObjectId = compositeObjectId,
                OrderedScanBatchId = batchId,
                OrderedScanGroupOrdinal = 1,
                AssemblyManifestHash = new string('c', 64),
                UploadCompletedAt = uploadCompletedAt,
                PageCount = 2,
                FinalizedAt = uploadCompletedAt,
                CreatedAt = uploadCompletedAt,
                UpdatedAt = uploadCompletedAt,
            });
            db.GradingRuns.Add(new GradingRunEntity
            {
                Id = UlidId.New(UtcNow.AddMilliseconds(39)),
                SubmissionId = submissionId,
                RunNumber = 1,
                TemplateVersionId = versionId,
                Reason = "initial",
                State = "finalized",
                PipelineVersion = "retention-test-v1",
                CanonicalInputManifestHash = new string('d', 64),
                EarnedPointsMilli = 3_500,
                PossiblePointsMilli = 5_000,
                CreatedAt = uploadCompletedAt,
                FinishedAt = uploadCompletedAt,
                FinalizedAt = uploadCompletedAt,
            });
            db.FileReferences.Add(new FileReferenceEntity
            {
                Id = compositeReferenceId,
                FileObjectId = compositeObjectId,
                OwnerType = "submission",
                OwnerId = submissionId,
                Purpose = "original_scan",
                RetentionAnchorAt = uploadCompletedAt,
                CreatedAt = uploadCompletedAt,
            });

            for (var pageNumber = 1; pageNumber <= pageWrites.Length; pageNumber++)
            {
                var write = pageWrites[pageNumber - 1];
                var objectId = UlidId.New(
                    UtcNow.AddMilliseconds(40 + pageNumber));
                var referenceId = UlidId.New(
                    UtcNow.AddMilliseconds(50 + pageNumber));
                var uploadSessionId = UlidId.New(
                    UtcNow.AddMilliseconds(60 + pageNumber));
                var itemId = UlidId.New(
                    UtcNow.AddMilliseconds(70 + pageNumber));
                itemIds[pageNumber - 1] = itemId;
                objectIds[pageNumber] = objectId;
                referenceIds[pageNumber] = referenceId;

                db.FileObjects.Add(CreateManagedFileObject(
                    objectId,
                    write,
                    uploadCompletedAt));
                db.FileReferences.Add(new FileReferenceEntity
                {
                    Id = referenceId,
                    FileObjectId = objectId,
                    OwnerType = "submission",
                    OwnerId = submissionId,
                    Purpose = "original_scan_page",
                    RetentionAnchorAt = uploadCompletedAt,
                    CreatedAt = uploadCompletedAt,
                });
                db.UploadSessions.Add(new UploadSessionEntity
                {
                    Id = uploadSessionId,
                    CreatedByStaffUserId = staffId,
                    Purpose = "ordered_scan_page",
                    TestSessionId = sessionId,
                    DestinationType = "ordered_scan_batch",
                    DestinationId = batchId,
                    OriginalFileName = $"scan-{pageNumber:000}.pdf",
                    DeclaredMimeType = "application/pdf",
                    ExpectedBytes = write.Locator.Bytes,
                    CurrentBytes = write.Locator.Bytes,
                    FinalSha256 = write.Locator.Sha256,
                    IncomingRelativePath = $"incoming/{uploadSessionId}.part",
                    State = "completed",
                    ExpiresAt = uploadCompletedAt.AddHours(24),
                    OrderedScanBatchId = batchId,
                    OrderedScanInputOrdinal = pageNumber,
                    OrderedScanClientItemId = $"client-{pageNumber}",
                    CreatedAt = uploadCompletedAt,
                    UpdatedAt = uploadCompletedAt,
                });
                db.OrderedScanItems.Add(new OrderedScanItemEntity
                {
                    Id = itemId,
                    BatchId = batchId,
                    InputOrdinal = pageNumber,
                    ClientItemId = $"client-{pageNumber}",
                    OriginalFileName = $"scan-{pageNumber:000}.pdf",
                    UploadSessionId = uploadSessionId,
                    SourceFileReferenceId = referenceId,
                    SourceSha256 = write.Locator.Sha256,
                    SourceBytes = write.Locator.Bytes,
                    UploadCompletedAt = uploadCompletedAt,
                    DetectedTemplatePageNumber = pageNumber,
                    ClassificationConfidenceBasisPoints = 10_000,
                    Status = OrderedScanItemStatus.Grouped,
                    GroupOrdinal = 1,
                    SubmissionId = submissionId,
                    SubmissionPageNumber = pageNumber,
                    CreatedAt = uploadCompletedAt,
                    UpdatedAt = uploadCompletedAt,
                });
                db.SubmissionSourcePages.Add(new SubmissionSourcePageEntity
                {
                    Id = UlidId.New(UtcNow.AddMilliseconds(80 + pageNumber)),
                    SubmissionId = submissionId,
                    PageNumber = pageNumber,
                    OrderedScanItemId = itemId,
                    UploadSessionId = uploadSessionId,
                    FileReferenceId = referenceId,
                    SourcePageNumber = 1,
                    SourceSha256 = write.Locator.Sha256,
                    AssemblyPolicyVersion =
                        OrderedScanAssemblyPlanner.CurrentPolicyVersion,
                    CreatedAt = uploadCompletedAt,
                });

                var submissionPageId = UlidId.New(
                    UtcNow.AddMilliseconds(90 + pageNumber));
                var normalizedObjectId = UlidId.New(
                    UtcNow.AddMilliseconds(100 + pageNumber));
                var normalizedReferenceId = UlidId.New(
                    UtcNow.AddMilliseconds(110 + pageNumber));
                var thumbnailObjectId = UlidId.New(
                    UtcNow.AddMilliseconds(120 + pageNumber));
                var thumbnailReferenceId = UlidId.New(
                    UtcNow.AddMilliseconds(130 + pageNumber));
                var normalizedIndex = pageWrites.Length + pageNumber;
                var thumbnailIndex = (pageWrites.Length * 2) + pageNumber;
                objectIds[normalizedIndex] = normalizedObjectId;
                referenceIds[normalizedIndex] = normalizedReferenceId;
                objectIds[thumbnailIndex] = thumbnailObjectId;
                referenceIds[thumbnailIndex] = thumbnailReferenceId;
                db.FileObjects.Add(CreateManagedFileObject(
                    normalizedObjectId,
                    normalizedWrites[pageNumber - 1],
                    uploadCompletedAt,
                    ContentStorageClass.ManagedScanDerived,
                    "image/png"));
                db.FileObjects.Add(CreateManagedFileObject(
                    thumbnailObjectId,
                    thumbnailWrites[pageNumber - 1],
                    uploadCompletedAt,
                    ContentStorageClass.ManagedScanDerived,
                    "image/png"));
                db.FileReferences.AddRange(
                    new FileReferenceEntity
                    {
                        Id = normalizedReferenceId,
                        FileObjectId = normalizedObjectId,
                        OwnerType = "submission_page",
                        OwnerId = submissionPageId,
                        Purpose = "normalized_page",
                        RetentionAnchorAt = uploadCompletedAt,
                        CreatedAt = uploadCompletedAt,
                    },
                    new FileReferenceEntity
                    {
                        Id = thumbnailReferenceId,
                        FileObjectId = thumbnailObjectId,
                        OwnerType = "submission_page",
                        OwnerId = submissionPageId,
                        Purpose = "page_thumbnail",
                        RetentionAnchorAt = uploadCompletedAt,
                        CreatedAt = uploadCompletedAt,
                    });
                db.SubmissionPages.Add(new SubmissionPageEntity
                {
                    Id = submissionPageId,
                    SubmissionId = submissionId,
                    PageNumber = pageNumber,
                    NormalizedFileReferenceId = normalizedReferenceId,
                    ThumbnailFileReferenceId = thumbnailReferenceId,
                    WidthPixels = 100,
                    HeightPixels = 140,
                    RotationDegrees = 0,
                    SourceSha256 = write.Locator.Sha256,
                    NormalizedSha256 = normalizedWrites[pageNumber - 1]
                        .Locator.Sha256,
                    DifferenceHash = $"{pageNumber:x16}",
                    PerceptualHash = $"{pageNumber:x16}",
                    QualityState = "accepted",
                    BlurBasisPoints = 5_000,
                    ContrastBasisPoints = 5_000,
                    BrightnessBasisPoints = 5_000,
                    InkCoverageBasisPoints = 5_000,
                    AlignmentState = "aligned",
                    AlignmentScoreBasisPoints = 10_000,
                    CreatedAt = uploadCompletedAt,
                });
            }

            db.BackgroundJobs.Add(new BackgroundJobEntity
            {
                Id = retentionJobId,
                Type = RetentionJobWorker.JobType,
                SchemaVersion = 1,
                DeduplicationKey = $"retention:ordered:{retentionJobId}",
                Priority = 100,
                PayloadJson = """{"reason":"test"}""",
                State = "queued",
                MaxAttempts = 3,
                NextAttemptAt = UtcNow,
                CreatedAt = UtcNow,
                UpdatedAt = UtcNow,
            });
            await db.SaveChangesAsync();

            return new SeededOrderedSubmission(
                submissionId,
                objectIds,
                referenceIds,
                [
                    compositeWrite.Locator,
                    .. pageWrites.Select(item => item.Locator),
                    .. normalizedWrites.Select(item => item.Locator),
                    .. thumbnailWrites.Select(item => item.Locator),
                ],
                itemIds,
                pageWrites.Select(item => item.Locator.Sha256).ToArray());
        }

        private static FileObjectEntity CreateManagedFileObject(
            string id,
            ContentWriteResult write,
            DateTimeOffset createdAt,
            ContentStorageClass storageClass =
                ContentStorageClass.ManagedScanOriginal,
            string verifiedMime = "application/pdf") =>
            new()
            {
                Id = id,
                Sha256 = write.Locator.Sha256,
                Bytes = write.Locator.Bytes,
                VerifiedMime = verifiedMime,
                Extension = write.Locator.Extension,
                RelativeObjectPath = write.RelativePath,
                StorageClass = storageClass.ToString(),
                RetentionClass = storageClass
                    == ContentStorageClass.ManagedScanDerived
                        ? "submitted_scan_derived"
                        : "submitted_scan",
                ManagedScanBytes = true,
                State = "available",
                CreatedAt = createdAt,
                VerifiedAt = createdAt,
                ReferenceCountCache = 1,
            };

        public async Task<string> EnqueueJobAsync(string type, int priority)
        {
            var id = UlidId.New(UtcNow.AddMilliseconds(20));
            await using var db = Factory.CreateDbContext();
            db.BackgroundJobs.Add(new BackgroundJobEntity
            {
                Id = id,
                Type = type,
                SchemaVersion = 1,
                DeduplicationKey = $"test:{type}:{id}",
                Priority = priority,
                PayloadJson = "{}",
                State = "queued",
                MaxAttempts = 3,
                NextAttemptAt = UtcNow,
                CreatedAt = UtcNow,
                UpdatedAt = UtcNow,
            });
            await db.SaveChangesAsync();
            return id;
        }

        public ValueTask DisposeAsync()
        {
            _writeCoordinator.Dispose();
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }

            return ValueTask.CompletedTask;
        }

        private static SubmissionEntity CreateSubmission(
            string id,
            string sessionId,
            string fileObjectId,
            DateTimeOffset uploadCompletedAt) =>
            new()
            {
                Id = id,
                TestSessionId = sessionId,
                State = "finalized",
                ScanPayloadState = "scan_available",
                AssignmentMethod = "none",
                AttemptNumber = 1,
                UploadedByStaffUserId = UlidId.New(uploadCompletedAt),
                OriginalFileName = "scan.bin",
                OriginalFileObjectId = fileObjectId,
                UploadCompletedAt = uploadCompletedAt,
                FinalizedAt = uploadCompletedAt,
                CreatedAt = uploadCompletedAt,
                UpdatedAt = uploadCompletedAt,
            };
    }

    private sealed record SeededSubmission(
        string SubmissionId,
        string? RecentSubmissionId,
        string FileObjectId,
        string RetentionJobId,
        ContentObjectLocator Locator);

    private sealed record SeededOrderedSubmission(
        string SubmissionId,
        string[] FileObjectIds,
        string[] FileReferenceIds,
        ContentObjectLocator[] Locators,
        string[] ItemIds,
        string[] SourceSha256s);

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class RetentionDbContextFactory(
        DbContextOptions<OokiGraderDbContext> options,
        IClock clock) : IDbContextFactory<OokiGraderDbContext>
    {
        public OokiGraderDbContext CreateDbContext() => new(options, clock);

        public Task<OokiGraderDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }

    private sealed class DeleteFailingContentStore(IContentStore inner)
        : IContentStore
    {
        public Task<ContentWriteResult> PutAsync(
            Stream source,
            ContentStorageClass storageClass,
            string verifiedExtension,
            CancellationToken cancellationToken = default) =>
            inner.PutAsync(
                source,
                storageClass,
                verifiedExtension,
                cancellationToken);

        public Task<Stream> OpenReadAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default) =>
            inner.OpenReadAsync(locator, cancellationToken);

        public Task<bool> ExistsAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default) =>
            inner.ExistsAsync(locator, cancellationToken);

        public Task DeleteAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default) =>
            throw new IOException("Simulated retention delete failure.");
    }
}
