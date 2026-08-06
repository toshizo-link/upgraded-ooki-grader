using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Host.Jobs;
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
                NullLogger<RetentionJobWorker>.Instance);
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
