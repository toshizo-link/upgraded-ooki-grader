using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OokiGrader.Application.Abstractions;
using OokiGrader.Host.Jobs;
using OokiGrader.Infrastructure.Backups;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Infrastructure.Storage;

namespace OokiGrader.IntegrationTests;

public sealed class BackupJobWorkerTests
{
    [Fact]
    public async Task ScheduledJobIsDeduplicatedAndProducesVerifiedRecord()
    {
        await using var fixture = await BackupWorkerFixture.CreateAsync();

        var first = await fixture.Coordinator.EnsureScheduledAsync();
        var second = await fixture.Coordinator.EnsureScheduledAsync();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.BackupId, second.BackupId);
        Assert.Equal(first.JobId, second.JobId);

        var processed = await fixture.Worker.ProcessNextAsync();

        Assert.True(processed);
        Assert.False(fixture.ArchiveObservedWriteCoordinator);
        await using var db = fixture.Factory.CreateDbContext();
        var record = await db.BackupRecords.AsNoTracking().SingleAsync();
        var job = await db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Id == record.BackgroundJobId);
        Assert.Equal(BackupStates.Verified, record.State);
        Assert.NotNull(record.ManifestSha256);
        Assert.Equal("ok", record.IntegrityResult);
        Assert.NotNull(record.VerifiedAt);
        Assert.Equal("succeeded", job.State);
        Assert.Equal(10_000, job.ProgressBasisPoints);
        Assert.Contains(
            fixture.Audit.Events,
            item => item.EventType == "backup.created"
                && item.ObjectId == record.Id);
    }

    [Fact]
    public async Task ManualJobRunsArchiveOutsideSerializedDatabaseWrite()
    {
        await using var fixture = await BackupWorkerFixture.CreateAsync();
        var enqueued = await fixture.Coordinator.EnqueueManualAsync(
            actorStaffUserId: null,
            correlationId: "backup-test");

        await fixture.Worker.ProcessNextAsync();

        Assert.False(fixture.ArchiveObservedWriteCoordinator);
        await using var db = fixture.Factory.CreateDbContext();
        var record = await db.BackupRecords
            .AsNoTracking()
            .SingleAsync(item => item.Id == enqueued.BackupId);
        Assert.Equal("manual", record.Trigger);
        Assert.Equal(BackupStates.Verified, record.State);
    }

    [Fact]
    public async Task SuccessfulPruningMarksOnlyDeletedVerifiedRecordExpired()
    {
        await using var fixture = await BackupWorkerFixture.CreateAsync();
        var first = await fixture.Coordinator.EnqueueManualAsync(
            actorStaffUserId: null,
            correlationId: "first");
        await fixture.Worker.ProcessNextAsync();
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var record = await db.BackupRecords
                .SingleAsync(item => item.Id == first.BackupId);
            record.CompletedAt = fixture.UtcNow.AddDays(-500);
            await db.SaveChangesAsync();
        }

        var second = await fixture.Coordinator.EnqueueManualAsync(
            actorStaffUserId: null,
            correlationId: "second");
        await fixture.Worker.ProcessNextAsync();

        Assert.False(fixture.ArchiveObservedWriteCoordinator);
        await using var verificationDb = fixture.Factory.CreateDbContext();
        var records = await verificationDb.BackupRecords
            .AsNoTracking()
            .ToDictionaryAsync(item => item.Id);
        Assert.Equal(BackupStates.Expired, records[first.BackupId].State);
        Assert.Equal(BackupStates.Verified, records[second.BackupId].State);
        Assert.False(Directory.Exists(Path.Combine(
            fixture.DestinationPath,
            records[first.BackupId].DestinationRelativePath!
                .Replace('/', Path.DirectorySeparatorChar))));
        Assert.Contains(
            fixture.Audit.Events,
            item => item.EventType == "backup.retention.completed"
                && item.Outcome == "success");
    }

    [Fact]
    public async Task RetentionDoesNotSelectAnUnverifiedRecord()
    {
        await using var fixture = await BackupWorkerFixture.CreateAsync();
        var first = await fixture.Coordinator.EnqueueManualAsync(
            actorStaffUserId: null,
            correlationId: "unverified");
        await fixture.Worker.ProcessNextAsync();
        string firstPath;
        await using (var db = fixture.Factory.CreateDbContext())
        {
            var record = await db.BackupRecords
                .SingleAsync(item => item.Id == first.BackupId);
            record.State = BackupStates.Failed;
            record.CompletedAt = fixture.UtcNow.AddDays(-500);
            firstPath = Path.Combine(
                fixture.DestinationPath,
                record.DestinationRelativePath!
                    .Replace('/', Path.DirectorySeparatorChar));
            await db.SaveChangesAsync();
        }

        await fixture.Coordinator.EnqueueManualAsync(
            actorStaffUserId: null,
            correlationId: "newest");
        await fixture.Worker.ProcessNextAsync();

        await using var verificationDb = fixture.Factory.CreateDbContext();
        var recordAfterPruning = await verificationDb.BackupRecords
            .AsNoTracking()
            .SingleAsync(item => item.Id == first.BackupId);
        Assert.Equal(BackupStates.Failed, recordAfterPruning.State);
        Assert.True(Directory.Exists(firstPath));
    }

    private sealed class BackupWorkerFixture : IAsyncDisposable
    {
        private BackupWorkerFixture(
            string rootPath,
            BackupDbContextFactory factory,
            BackupJobCoordinator coordinator,
            BackupJobWorker worker,
            TrackingBackupArchive archive,
            TrackingWriteCoordinator writeCoordinator,
            RecordingAuditSink audit,
            DateTimeOffset utcNow,
            string destinationPath)
        {
            RootPath = rootPath;
            UtcNow = utcNow;
            DestinationPath = destinationPath;
            Factory = factory;
            Coordinator = coordinator;
            Worker = worker;
            Archive = archive;
            WriteCoordinator = writeCoordinator;
            Audit = audit;
        }

        public string RootPath { get; }
        public DateTimeOffset UtcNow { get; }
        public string DestinationPath { get; }
        public BackupDbContextFactory Factory { get; }
        public BackupJobCoordinator Coordinator { get; }
        public BackupJobWorker Worker { get; }
        public TrackingBackupArchive Archive { get; }
        public TrackingWriteCoordinator WriteCoordinator { get; }
        public RecordingAuditSink Audit { get; }
        public bool ArchiveObservedWriteCoordinator =>
            Archive.ObservedInsideWriteCoordinator;

        public static async Task<BackupWorkerFixture> CreateAsync()
        {
            var rootPath = Path.Combine(
                Path.GetTempPath(),
                "ooki-backup-worker-tests",
                Guid.NewGuid().ToString("N"));
            var dataPath = Path.Combine(rootPath, "data");
            var databasePath = Path.Combine(dataPath, "ooki.db");
            var objectPath = Path.Combine(dataPath, "objects");
            var destinationPath = Path.Combine(rootPath, "encrypted-backups");
            Directory.CreateDirectory(dataPath);
            var utcNow = new DateTimeOffset(
                2026,
                7,
                27,
                2,
                30,
                0,
                TimeSpan.Zero);
            var clock = new FixedClock(utcNow);
            var dbOptions = new DbContextOptionsBuilder<OokiGraderDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    ForeignKeys = true,
                    Pooling = false,
                }.ToString())
                .AddInterceptors(new SqlitePragmaConnectionInterceptor())
                .Options;
            var factory = new BackupDbContextFactory(dbOptions, clock);
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                db.SiteSettings.Add(new SiteSettingsEntity
                {
                    Id = "site",
                    SchoolName = "Backup Worker Test",
                    TimeZone = "UTC",
                    DataRoot = dataPath,
                    CreatedAt = utcNow,
                    UpdatedAt = utcNow,
                });
                await db.SaveChangesAsync();
            }

            var backupOptions = new BackupOptions
            {
                DatabasePath = databasePath,
                ContentRootPath = objectPath,
                DestinationRootPath = destinationPath,
                Enabled = true,
                DestinationEncryptionConfirmed = true,
                ApplicationVersion = "backup-worker-test",
            };
            var contentStore = new NtfsContentStore(new ContentStoreOptions
            {
                RootPath = objectPath,
            });
            var timeProvider = new FixedTimeProvider(utcNow);
            var writeCoordinator = new TrackingWriteCoordinator();
            var innerArchive = new SqliteOnlineBackupArchiveService(
                backupOptions,
                contentStore,
                timeProvider);
            var archive = new TrackingBackupArchive(
                innerArchive,
                writeCoordinator);
            var coordinator = new BackupJobCoordinator(
                factory,
                writeCoordinator,
                archive,
                backupOptions,
                timeProvider);
            var audit = new RecordingAuditSink();
            var worker = new BackupJobWorker(
                factory,
                writeCoordinator,
                archive,
                archive,
                backupOptions,
                coordinator,
                audit,
                timeProvider,
                NullLogger<BackupJobWorker>.Instance);
            return new BackupWorkerFixture(
                rootPath,
                factory,
                coordinator,
                worker,
                archive,
                writeCoordinator,
                audit,
                utcNow,
                destinationPath);
        }

        public async ValueTask DisposeAsync()
        {
            await Worker.StopAsync(CancellationToken.None);
            Worker.Dispose();
            WriteCoordinator.Dispose();
            try
            {
                if (Directory.Exists(RootPath))
                {
                    Directory.Delete(RootPath, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class BackupDbContextFactory(
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

    private sealed class TrackingWriteCoordinator :
        IWriteCoordinator,
        IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly AsyncLocal<int> _depth = new();

        public bool IsInside => _depth.Value > 0;

        public async Task ExecuteAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default)
        {
            await ExecuteAsync(
                async token =>
                {
                    await operation(token);
                    return true;
                },
                cancellationToken);
        }

        public async Task<T> ExecuteAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                _depth.Value++;
                return await operation(cancellationToken);
            }
            finally
            {
                _depth.Value--;
                _gate.Release();
            }
        }

        public void Dispose()
        {
            _gate.Dispose();
        }
    }

    private sealed class TrackingBackupArchive(
        IBackupArchiveService inner,
        TrackingWriteCoordinator writeCoordinator) :
        IBackupArchiveService,
        IBackupRetentionService
    {
        public bool ObservedInsideWriteCoordinator { get; private set; }

        public BackupConfigurationStatus GetConfigurationStatus() =>
            inner.GetConfigurationStatus();

        public Task<BackupCreationResult> CreateAsync(
            BackupCreationRequest request,
            CancellationToken cancellationToken = default)
        {
            ObservedInsideWriteCoordinator |= writeCoordinator.IsInside;
            return inner.CreateAsync(request, cancellationToken);
        }

        public Task<BackupVerificationResult> VerifyAsync(
            string backupId,
            string destinationRelativePath,
            string? expectedManifestSha256 = null,
            CancellationToken cancellationToken = default)
        {
            ObservedInsideWriteCoordinator |= writeCoordinator.IsInside;
            return inner.VerifyAsync(
                backupId,
                destinationRelativePath,
                expectedManifestSha256,
                cancellationToken);
        }

        public Task<BackupRestorePlan> ValidateRestorePlanAsync(
            string backupId,
            string destinationRelativePath,
            string? expectedManifestSha256 = null,
            CancellationToken cancellationToken = default)
        {
            ObservedInsideWriteCoordinator |= writeCoordinator.IsInside;
            return inner.ValidateRestorePlanAsync(
                backupId,
                destinationRelativePath,
                expectedManifestSha256,
                cancellationToken);
        }

        public Task<BackupRetentionResult> PruneAsync(
            IReadOnlyList<BackupRetentionCandidate> candidates,
            CancellationToken cancellationToken = default)
        {
            ObservedInsideWriteCoordinator |= writeCoordinator.IsInside;
            return ((IBackupRetentionService)inner).PruneAsync(
                candidates,
                cancellationToken);
        }
    }

    private sealed class RecordingAuditSink : IAuditSink
    {
        public List<AuditWrite> Events { get; } = [];

        public Task<string> AppendAsync(
            AuditWrite auditEvent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(auditEvent);
            return Task.FromResult($"audit-{Events.Count}");
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
