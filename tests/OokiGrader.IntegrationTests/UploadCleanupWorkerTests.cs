using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Host.Jobs;
using OokiGrader.Host.Uploads;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Infrastructure.Storage;

namespace OokiGrader.IntegrationTests;

public sealed class UploadCleanupWorkerTests
{
    [Fact]
    public async Task ExpiredUploadingPayloadTransitionsBeforeDeletionAndIsIdempotent()
    {
        await using var fixture = await UploadCleanupFixture.CreateAsync();
        var uploadId = await fixture.SeedUploadAsync(
            state: "uploading",
            expiresAt: fixture.UtcNow.AddMinutes(-1),
            createPayload: true);
        var path = fixture.IncomingPath(uploadId);

        var first = await fixture.Worker.ReconcileAsync();
        var second = await fixture.Worker.ReconcileAsync();

        Assert.Equal(
            new UploadCleanupResult(
                ExpiredSessionCount: 1,
                ReconciledTrackedFileCount: 1,
                DeletedOrphanFileCount: 0,
                DeletedContentStoreTemporaryFileCount: 0,
                FailureCount: 0),
            first);
        Assert.Equal(
            new UploadCleanupResult(0, 0, 0, 0, 0),
            second);
        Assert.False(File.Exists(path));

        await using var db = fixture.Factory.CreateDbContext();
        var upload = await db.UploadSessions.AsNoTracking().SingleAsync();
        Assert.Equal("expired", upload.State);
        Assert.Equal(string.Empty, upload.IncomingRelativePath);
        Assert.Equal(3, upload.Revision);

        var auditEvents = await db.AuditEvents
            .AsNoTracking()
            .OrderBy(item => item.EventType)
            .ToListAsync();
        Assert.Equal(
            ["upload.expired", "upload.temporary_file_deleted"],
            auditEvents.Select(item => item.EventType));
        Assert.All(auditEvents, item => Assert.Equal(uploadId, item.ObjectId));
        Assert.Single(await db.OutboxEvents.AsNoTracking().ToListAsync());
        Assert.Contains(
            "\"state\":\"expired\"",
            (await db.OutboxEvents.AsNoTracking().SingleAsync()).PayloadJson);
    }

    [Fact]
    public async Task MissingFinalizingPayloadIsReconciledAfterExpiry()
    {
        await using var fixture = await UploadCleanupFixture.CreateAsync();
        await fixture.SeedUploadAsync(
            state: "finalizing",
            expiresAt: fixture.UtcNow.AddHours(-1),
            createPayload: false);

        var result = await fixture.Worker.ReconcileAsync();

        Assert.Equal(1, result.ExpiredSessionCount);
        Assert.Equal(1, result.ReconciledTrackedFileCount);
        Assert.Equal(0, result.FailureCount);
        await using var db = fixture.Factory.CreateDbContext();
        var upload = await db.UploadSessions.AsNoTracking().SingleAsync();
        Assert.Equal("expired", upload.State);
        Assert.Equal(string.Empty, upload.IncomingRelativePath);
        var cleanup = await db.AuditEvents
            .AsNoTracking()
            .SingleAsync(item => item.EventType == "upload.temporary_file_deleted");
        Assert.Equal("reconciled_already_missing", cleanup.ReasonCode);
        Assert.Contains("\"alreadyMissing\":true", cleanup.SafeMetadataJson);
    }

    [Fact]
    public async Task ActiveUploadAndUnsafeTrackedPathAreNeverDeleted()
    {
        await using var fixture = await UploadCleanupFixture.CreateAsync();
        var activeId = await fixture.SeedUploadAsync(
            state: "uploading",
            expiresAt: fixture.UtcNow.AddHours(1),
            createPayload: true);
        var outsidePath = Path.Combine(fixture.RootPath, "outside.part");
        await File.WriteAllBytesAsync(outsidePath, [9, 8, 7]);
        var unsafeId = await fixture.SeedUploadAsync(
            state: "uploading",
            expiresAt: fixture.UtcNow.AddHours(-1),
            createPayload: false,
            relativePath: "../outside.part");

        var result = await fixture.Worker.ReconcileAsync();

        Assert.Equal(1, result.ExpiredSessionCount);
        Assert.Equal(0, result.ReconciledTrackedFileCount);
        Assert.Equal(1, result.FailureCount);
        Assert.True(File.Exists(fixture.IncomingPath(activeId)));
        Assert.True(File.Exists(outsidePath));
        await using var db = fixture.Factory.CreateDbContext();
        Assert.Equal(
            "uploading",
            await db.UploadSessions
                .Where(item => item.Id == activeId)
                .Select(item => item.State)
                .SingleAsync());
        var unsafeUpload = await db.UploadSessions
            .AsNoTracking()
            .SingleAsync(item => item.Id == unsafeId);
        Assert.Equal("expired", unsafeUpload.State);
        Assert.Equal("../outside.part", unsafeUpload.IncomingRelativePath);
    }

    [Fact]
    public async Task OldCanonicalOrphansAreDeletedWithoutTouchingOtherFiles()
    {
        await using var fixture = await UploadCleanupFixture.CreateAsync();
        var oldIncomingId = UlidId.New(fixture.UtcNow.AddDays(-2));
        var oldIncoming = fixture.IncomingPath(oldIncomingId);
        await File.WriteAllBytesAsync(oldIncoming, [1, 2, 3]);
        File.SetLastWriteTimeUtc(
            oldIncoming,
            fixture.UtcNow.AddHours(-25).UtcDateTime);
        var youngIncomingId = UlidId.New(fixture.UtcNow);
        var youngIncoming = fixture.IncomingPath(youngIncomingId);
        await File.WriteAllBytesAsync(youngIncoming, [4]);
        var nonCanonicalIncoming = Path.Combine(
            fixture.IncomingRoot,
            "untracked.part");
        await File.WriteAllBytesAsync(nonCanonicalIncoming, [5]);
        File.SetLastWriteTimeUtc(
            nonCanonicalIncoming,
            fixture.UtcNow.AddHours(-25).UtcDateTime);

        var contentTemporaryRoot = Path.Combine(
            fixture.ObjectStoreRoot,
            "incoming",
            "objects");
        Directory.CreateDirectory(contentTemporaryRoot);
        var oldContentTemporary = Path.Combine(
            contentTemporaryRoot,
            $"{Guid.NewGuid():N}.part");
        await File.WriteAllBytesAsync(oldContentTemporary, [6, 7]);
        File.SetLastWriteTimeUtc(
            oldContentTemporary,
            fixture.UtcNow.AddHours(-25).UtcDateTime);
        var nonCanonicalContentTemporary = Path.Combine(
            contentTemporaryRoot,
            "keep.part");
        await File.WriteAllBytesAsync(nonCanonicalContentTemporary, [8]);
        File.SetLastWriteTimeUtc(
            nonCanonicalContentTemporary,
            fixture.UtcNow.AddHours(-25).UtcDateTime);

        var result = await fixture.Worker.ReconcileAsync();

        Assert.Equal(1, result.DeletedOrphanFileCount);
        Assert.Equal(1, result.DeletedContentStoreTemporaryFileCount);
        Assert.Equal(0, result.FailureCount);
        Assert.False(File.Exists(oldIncoming));
        Assert.False(File.Exists(oldContentTemporary));
        Assert.True(File.Exists(youngIncoming));
        Assert.True(File.Exists(nonCanonicalIncoming));
        Assert.True(File.Exists(nonCanonicalContentTemporary));

        await using var db = fixture.Factory.CreateDbContext();
        var eventTypes = await db.AuditEvents
            .AsNoTracking()
            .Select(item => item.EventType)
            .ToListAsync();
        Assert.Contains("upload.orphan_temporary_file_deleted", eventTypes);
        Assert.Contains("storage.orphan_temporary_file_deleted", eventTypes);
    }

    [Fact]
    public async Task CommittedAndRecentObjectsArePreservedWhileStaleOrphanIsQuarantined()
    {
        await using var fixture = await UploadCleanupFixture.CreateAsync();
        var committed = await fixture.PutObjectAsync([1, 2, 3]);
        var recent = await fixture.PutObjectAsync([4, 5, 6]);
        var orphan = await fixture.PutObjectAsync([7, 8, 9]);
        fixture.MakeObjectStale(committed);
        fixture.MakeObjectStale(orphan);
        await fixture.TrackCommittedObjectAsync(committed);

        var first = await fixture.Worker.ReconcileAsync();
        var second = await fixture.Worker.ReconcileAsync();

        Assert.True(File.Exists(fixture.ObjectPath(committed)));
        Assert.True(File.Exists(fixture.ObjectPath(recent)));
        Assert.False(File.Exists(fixture.ObjectPath(orphan)));
        Assert.True(File.Exists(fixture.QuarantinePath(orphan)));
        Assert.Equal(1, first.QuarantinedPromotedObjectCount);
        Assert.Equal(0, first.FailureCount);
        Assert.Equal(0, second.QuarantinedPromotedObjectCount);
        Assert.Equal(0, second.DeletedQuarantinedObjectCount);
        Assert.Equal(0, second.FailureCount);

        await using var db = fixture.Factory.CreateDbContext();
        var audit = await db.AuditEvents
            .AsNoTracking()
            .SingleAsync(item =>
                item.EventType
                == "storage.unreferenced_promoted_object_quarantined");
        Assert.Equal(orphan.Locator.Sha256, audit.ObjectId);
        Assert.Equal("orphan_cleanup", audit.ReasonCode);
    }

    [Fact]
    public async Task QuarantinedObjectIsDeletedOnlyAfterRetentionAndFinalRecheck()
    {
        await using var fixture = await UploadCleanupFixture.CreateAsync();
        var orphan = await fixture.PutObjectAsync([10, 11, 12]);
        fixture.MakeObjectStale(orphan);

        var quarantined = await fixture.Worker.ReconcileAsync();
        fixture.Advance(TimeSpan.FromDays(8));
        var deleted = await fixture.Worker.ReconcileAsync();
        var redelivery = await fixture.Worker.ReconcileAsync();

        Assert.Equal(1, quarantined.QuarantinedPromotedObjectCount);
        Assert.Equal(1, deleted.DeletedQuarantinedObjectCount);
        Assert.Equal(0, redelivery.DeletedQuarantinedObjectCount);
        Assert.False(File.Exists(fixture.ObjectPath(orphan)));
        Assert.False(File.Exists(fixture.QuarantinePath(orphan)));
    }

    [Fact]
    public async Task LateDatabaseCommitRestoresRecoverableQuarantine()
    {
        await using var fixture = await UploadCleanupFixture.CreateAsync();
        var promoted = await fixture.PutObjectAsync([13, 14, 15]);
        fixture.MakeObjectStale(promoted);
        await fixture.Worker.ReconcileAsync();
        Assert.True(File.Exists(fixture.QuarantinePath(promoted)));

        await fixture.TrackCommittedObjectAsync(promoted);
        var restored = await fixture.Worker.ReconcileAsync();

        Assert.Equal(1, restored.RestoredPromotedObjectCount);
        Assert.True(File.Exists(fixture.ObjectPath(promoted)));
        Assert.False(File.Exists(fixture.QuarantinePath(promoted)));
        await using var db = fixture.Factory.CreateDbContext();
        Assert.Single(await db.AuditEvents
            .AsNoTracking()
            .Where(item => item.EventType == "storage.promoted_object_restored")
            .ToListAsync());
    }

    [Fact]
    public async Task DiscoveryHashingAndQuarantineStayOutsideWriteCoordinator()
    {
        await using var fixture = await UploadCleanupFixture.CreateAsync();
        var promoted = await fixture.PutObjectAsync([16, 17, 18]);
        fixture.MakeObjectStale(promoted);
        using var coordinator = new ObservingWriteCoordinator();
        var fileSystem = new GuardedReconciliationFileSystem(
            new NtfsPromotedContentObjectFileSystem(),
            coordinator);
        var reconciler = new PromotedContentObjectReconciler(
            fixture.Factory,
            coordinator,
            fixture.Configuration,
            fixture.Environment,
            fixture.TimeProvider,
            fileSystem);

        var result = await reconciler.ReconcileAsync();

        Assert.Equal(1, result.QuarantinedObjectCount);
        Assert.True(coordinator.WasEntered);
        Assert.True(fileSystem.HashWasCalled);
        Assert.False(fileSystem.ObservedInsideWriteCoordinator);
    }

    [Fact]
    public async Task PhysicalInventoryHonorsCandidateBound()
    {
        await using var fixture = await UploadCleanupFixture.CreateAsync();
        var first = await fixture.PutObjectAsync([21]);
        var second = await fixture.PutObjectAsync([22]);
        var third = await fixture.PutObjectAsync([23]);
        fixture.MakeObjectStale(first);
        fixture.MakeObjectStale(second);
        fixture.MakeObjectStale(third);
        var fileSystem = new NtfsPromotedContentObjectFileSystem();

        var candidates = fileSystem.DiscoverPromotedObjects(
            fixture.ObjectStoreRoot,
            fixture.UtcNow.AddHours(-24),
            maximumEntries: 100,
            maximumCandidates: 2);

        Assert.Equal(2, candidates.Count);
    }

    private sealed class UploadCleanupFixture : IAsyncDisposable
    {
        private readonly SemaphoreWriteCoordinator _writeCoordinator;
        private readonly FixedTimeProvider _timeProvider;

        private UploadCleanupFixture(
            string rootPath,
            DateTimeOffset utcNow,
            UploadCleanupDbContextFactory factory,
            SemaphoreWriteCoordinator writeCoordinator,
            FixedTimeProvider timeProvider,
            IConfiguration configuration,
            IHostEnvironment environment,
            NtfsContentStore contentStore,
            UploadCleanupWorker worker)
        {
            RootPath = rootPath;
            UtcNow = utcNow;
            Factory = factory;
            _writeCoordinator = writeCoordinator;
            _timeProvider = timeProvider;
            Configuration = configuration;
            Environment = environment;
            ContentStore = contentStore;
            Worker = worker;
        }

        public string RootPath { get; }

        public string IncomingRoot => Path.Combine(RootPath, "incoming");

        public string ObjectStoreRoot => Path.Combine(RootPath, "objects");

        public DateTimeOffset UtcNow { get; }

        public UploadCleanupDbContextFactory Factory { get; }

        public IConfiguration Configuration { get; }

        public IHostEnvironment Environment { get; }

        public TimeProvider TimeProvider => _timeProvider;

        public NtfsContentStore ContentStore { get; }

        public UploadCleanupWorker Worker { get; }

        public string PromotedObjectQuarantineRoot =>
            Path.Combine(RootPath, "quarantine", "promoted-objects");

        public static async Task<UploadCleanupFixture> CreateAsync()
        {
            var rootPath = Path.Combine(
                Path.GetTempPath(),
                $"ooki-upload-cleanup-{Guid.NewGuid():N}");
            Directory.CreateDirectory(rootPath);
            var incomingRoot = Path.Combine(rootPath, "incoming");
            Directory.CreateDirectory(incomingRoot);
            var objectStoreRoot = Path.Combine(rootPath, "objects");
            Directory.CreateDirectory(objectStoreRoot);
            var utcNow = new DateTimeOffset(
                2026,
                7,
                27,
                6,
                0,
                0,
                TimeSpan.Zero);
            var clock = new FixedClock(utcNow);
            var options = new DbContextOptionsBuilder<OokiGraderDbContext>()
                .UseSqlite($"Data Source={Path.Combine(rootPath, "test.db")}")
                .Options;
            var factory = new UploadCleanupDbContextFactory(options, clock);
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
            }

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Data:Root"] = rootPath,
                    ["Data:Incoming"] = incomingRoot,
                    ["Data:ObjectStore"] = objectStoreRoot,
                })
                .Build();
            var environment = new TestHostEnvironment
            {
                ContentRootPath = rootPath,
                ContentRootFileProvider = new PhysicalFileProvider(rootPath),
            };
            var writeCoordinator = new SemaphoreWriteCoordinator();
            var timeProvider = new FixedTimeProvider(utcNow);
            var contentStore = new NtfsContentStore(new ContentStoreOptions
            {
                RootPath = objectStoreRoot,
            });
            var worker = new UploadCleanupWorker(
                factory,
                writeCoordinator,
                new UploadLockProvider(),
                configuration,
                environment,
                timeProvider,
                NullLogger<UploadCleanupWorker>.Instance);
            return new UploadCleanupFixture(
                rootPath,
                utcNow,
                factory,
                writeCoordinator,
                timeProvider,
                configuration,
                environment,
                contentStore,
                worker);
        }

        public string IncomingPath(string uploadId)
        {
            return Path.Combine(IncomingRoot, $"{uploadId}.part");
        }

        public string ObjectPath(ContentWriteResult stored)
        {
            return Path.Combine(
                ObjectStoreRoot,
                stored.RelativePath.Replace(
                    '/',
                    Path.DirectorySeparatorChar));
        }

        public string QuarantinePath(ContentWriteResult stored)
        {
            return Path.Combine(
                    PromotedObjectQuarantineRoot,
                    stored.RelativePath.Replace(
                        '/',
                        Path.DirectorySeparatorChar))
                + ".orphan";
        }

        public async Task<ContentWriteResult> PutObjectAsync(byte[] payload)
        {
            await using var source = new MemoryStream(payload);
            return await ContentStore.PutAsync(
                source,
                ContentStorageClass.ManagedScanDerived,
                "png");
        }

        public void MakeObjectStale(ContentWriteResult stored)
        {
            File.SetLastWriteTimeUtc(
                ObjectPath(stored),
                UtcNow.AddHours(-25).UtcDateTime);
        }

        public async Task TrackCommittedObjectAsync(ContentWriteResult stored)
        {
            await using var db = Factory.CreateDbContext();
            db.FileObjects.Add(new FileObjectEntity
            {
                Id = UlidId.New(_timeProvider.GetUtcNow()),
                Sha256 = stored.Locator.Sha256,
                Bytes = stored.Locator.Bytes,
                VerifiedMime = "image/png",
                Extension = stored.Locator.Extension,
                RelativeObjectPath = stored.RelativePath,
                StorageClass = stored.Locator.StorageClass.ToString(),
                RetentionClass = "submission_derived",
                ManagedScanBytes = true,
                State = "available",
                CreatedAt = _timeProvider.GetUtcNow(),
                VerifiedAt = _timeProvider.GetUtcNow(),
            });
            await db.SaveChangesAsync();
        }

        public void Advance(TimeSpan duration)
        {
            _timeProvider.Advance(duration);
        }

        public async Task<string> SeedUploadAsync(
            string state,
            DateTimeOffset expiresAt,
            bool createPayload,
            string? relativePath = null)
        {
            var uploadId = UlidId.New(
                UtcNow.AddMilliseconds(
                    Random.Shared.Next(1, 10_000)));
            relativePath ??= $"{uploadId}.part";
            if (createPayload)
            {
                await File.WriteAllBytesAsync(
                    Path.Combine(IncomingRoot, relativePath),
                    [1, 2, 3, 4]);
            }

            await using var db = Factory.CreateDbContext();
            db.UploadSessions.Add(new UploadSessionEntity
            {
                Id = uploadId,
                CreatedByStaffUserId = UlidId.New(UtcNow),
                Purpose = "template_source",
                OriginalFileName = "source.pdf",
                DeclaredMimeType = "application/pdf",
                ExpectedBytes = 4,
                CurrentBytes = createPayload ? 4 : 0,
                IncomingRelativePath = relativePath,
                State = state,
                ExpiresAt = expiresAt,
                CreatedAt = UtcNow.AddHours(-25),
                UpdatedAt = UtcNow.AddHours(-25),
            });
            await db.SaveChangesAsync();
            return uploadId;
        }

        public ValueTask DisposeAsync()
        {
            Worker.Dispose();
            _writeCoordinator.Dispose();
            try
            {
                Directory.Delete(RootPath, recursive: true);
            }
            catch (IOException)
            {
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class UploadCleanupDbContextFactory(
        DbContextOptions<OokiGraderDbContext> options,
        IClock clock) : IDbContextFactory<OokiGraderDbContext>
    {
        public OokiGraderDbContext CreateDbContext()
        {
            return new OokiGraderDbContext(options, clock);
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow => utcNow;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration)
        {
            _utcNow = _utcNow.Add(duration);
        }
    }

    private sealed class ObservingWriteCoordinator :
        IWriteCoordinator,
        IDisposable
    {
        private readonly SemaphoreSlim _mutex = new(1, 1);
        private readonly AsyncLocal<int> _depth = new();

        public bool IsHeld => _depth.Value > 0;

        public bool WasEntered { get; private set; }

        public async Task ExecuteAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default)
        {
            await _mutex.WaitAsync(cancellationToken);
            try
            {
                WasEntered = true;
                _depth.Value++;
                await operation(cancellationToken);
            }
            finally
            {
                _depth.Value--;
                _mutex.Release();
            }
        }

        public async Task<T> ExecuteAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            await _mutex.WaitAsync(cancellationToken);
            try
            {
                WasEntered = true;
                _depth.Value++;
                return await operation(cancellationToken);
            }
            finally
            {
                _depth.Value--;
                _mutex.Release();
            }
        }

        public void Dispose()
        {
            _mutex.Dispose();
        }
    }

    private sealed class GuardedReconciliationFileSystem(
        IPromotedContentObjectFileSystem inner,
        ObservingWriteCoordinator coordinator)
        : IPromotedContentObjectFileSystem
    {
        public bool HashWasCalled { get; private set; }

        public bool ObservedInsideWriteCoordinator { get; private set; }

        public IReadOnlyList<PromotedContentObjectCandidate>
            DiscoverPromotedObjects(
                string objectStoreRoot,
                DateTimeOffset cutoff,
                int maximumEntries,
                int maximumCandidates)
        {
            Observe();
            return inner.DiscoverPromotedObjects(
                objectStoreRoot,
                cutoff,
                maximumEntries,
                maximumCandidates);
        }

        public IReadOnlyList<QuarantinedContentObjectCandidate>
            DiscoverQuarantinedObjects(
                string quarantineRoot,
                int maximumEntries,
                int maximumCandidates)
        {
            Observe();
            return inner.DiscoverQuarantinedObjects(
                quarantineRoot,
                maximumEntries,
                maximumCandidates);
        }

        public Task<string> ComputeSha256Async(
            string root,
            string absolutePath,
            CancellationToken cancellationToken)
        {
            HashWasCalled = true;
            Observe();
            return inner.ComputeSha256Async(
                root,
                absolutePath,
                cancellationToken);
        }

        public Task<QuarantinedContentObjectCandidate> QuarantineAsync(
            string objectStoreRoot,
            string quarantineRoot,
            PromotedContentObjectCandidate candidate,
            string actualSha256,
            DateTimeOffset quarantinedAt,
            CancellationToken cancellationToken)
        {
            Observe();
            return inner.QuarantineAsync(
                objectStoreRoot,
                quarantineRoot,
                candidate,
                actualSha256,
                quarantinedAt,
                cancellationToken);
        }

        public Task RestoreAsync(
            string objectStoreRoot,
            string quarantineRoot,
            QuarantinedContentObjectCandidate candidate,
            CancellationToken cancellationToken)
        {
            Observe();
            return inner.RestoreAsync(
                objectStoreRoot,
                quarantineRoot,
                candidate,
                cancellationToken);
        }

        public void DeleteQuarantined(
            string quarantineRoot,
            QuarantinedContentObjectCandidate candidate)
        {
            Observe();
            inner.DeleteQuarantined(quarantineRoot, candidate);
        }

        private void Observe()
        {
            ObservedInsideWriteCoordinator |= coordinator.IsHeld;
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "OokiGrader.IntegrationTests";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
