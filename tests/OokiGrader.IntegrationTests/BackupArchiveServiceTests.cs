using System.Text;
using System.Text.Json;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Infrastructure.Backups;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Infrastructure.Storage;

namespace OokiGrader.IntegrationTests;

public sealed class BackupArchiveServiceTests
{
    [Fact]
    public async Task OnlineBackupIncludesWalDataAndExcludesManagedScansByDefault()
    {
        await using var fixture = await BackupFixture.CreateAsync();
        var template = await fixture.AddObjectAsync(
            ContentStorageClass.TemplateSource,
            "template_source",
            managedScanBytes: false,
            "template-source");
        var scan = await fixture.AddObjectAsync(
            ContentStorageClass.ManagedScanOriginal,
            "submitted_scan",
            managedScanBytes: true,
            "student-handwriting");
        await using var walConnection = await fixture.OpenWalWriterAsync();
        await using (var insert = walConnection.CreateCommand())
        {
            insert.CommandText =
                """
                INSERT INTO audit_event
                    (id, occurred_at, event_type, object_type, object_id, outcome)
                VALUES
                    ($id, $occurred_at, 'backup.test', 'fixture', 'wal', 'success');
                """;
            insert.Parameters.AddWithValue(
                "$id",
                UlidId.New(fixture.UtcNow.AddMilliseconds(20)));
            insert.Parameters.AddWithValue(
                "$occurred_at",
                fixture.UtcNow.ToUnixTimeMilliseconds());
            await insert.ExecuteNonQueryAsync();
        }

        Assert.True(File.Exists(fixture.DatabasePath + "-wal"));

        var backupId = UlidId.New(fixture.UtcNow.AddMinutes(1));
        var result = await fixture.Service.CreateAsync(
            new BackupCreationRequest(
                backupId,
                "manual",
                fixture.UtcNow));

        Assert.True(result.Verification.Verified);
        Assert.Equal("ok", result.Verification.IntegrityResult);
        Assert.Equal(1, result.ObjectCount);
        Assert.Equal(template.Locator.Bytes, result.ObjectBytes);
        Assert.NotEmpty(result.Verification.ManifestSha256);
        var backupRoot = fixture.ResolveBackup(result.DestinationRelativePath);
        var manifest = JsonSerializer.Deserialize<BackupManifest>(
            await File.ReadAllBytesAsync(
                Path.Combine(backupRoot, "manifest.json")),
            JsonOptions);
        Assert.NotNull(manifest);
        Assert.False(manifest.ManagedScansIncluded);
        Assert.Contains(
            manifest.Files,
            item => item.FileObjectId == template.FileObjectId);
        Assert.DoesNotContain(
            manifest.Files,
            item => item.FileObjectId == scan.FileObjectId);

        var snapshotPath = Path.Combine(
            backupRoot,
            "database",
            "ooki-grader.db");
        await using var snapshot = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = snapshotPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
        await snapshot.OpenAsync();
        await using var query = snapshot.CreateCommand();
        query.CommandText =
            "SELECT COUNT(*) FROM audit_event WHERE object_id = 'wal';";
        Assert.Equal(1L, (long)(await query.ExecuteScalarAsync())!);

        var plan = await fixture.Service.ValidateRestorePlanAsync(
            backupId,
            result.DestinationRelativePath,
            result.ManifestSha256);
        Assert.True(plan.CanRestore);
        Assert.False(plan.ManagedScansIncluded);
        Assert.Contains(
            plan.RequiredActions,
            action => action.Contains(
                "maintenance mode",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VerificationRejectsAChangedObject()
    {
        await using var fixture = await BackupFixture.CreateAsync();
        await fixture.AddObjectAsync(
            ContentStorageClass.TemplateSource,
            "template_source",
            managedScanBytes: false,
            "template-source");
        var backupId = UlidId.New(fixture.UtcNow.AddMinutes(1));
        var result = await fixture.Service.CreateAsync(
            new BackupCreationRequest(
                backupId,
                "manual",
                fixture.UtcNow));
        var backupRoot = fixture.ResolveBackup(result.DestinationRelativePath);
        var manifest = JsonSerializer.Deserialize<BackupManifest>(
            await File.ReadAllBytesAsync(
                Path.Combine(backupRoot, "manifest.json")),
            JsonOptions)!;
        var objectEntry = manifest.Files.Single(
            item => item.Role == "content_object");
        var objectPath = Path.Combine(
            backupRoot,
            objectEntry.RelativePath.Replace(
                '/',
                Path.DirectorySeparatorChar));
        await File.AppendAllTextAsync(objectPath, "tampered");

        var verification = await fixture.Service.VerifyAsync(
            backupId,
            result.DestinationRelativePath,
            result.ManifestSha256);

        Assert.False(verification.Verified);
        Assert.Equal("backup_file_length_mismatch", verification.ErrorCode);
    }

    [Fact]
    public async Task CreationRequiresConfirmedEncryptedDestination()
    {
        await using var fixture = await BackupFixture.CreateAsync(
            encryptionConfirmed: false);

        var exception = await Assert.ThrowsAsync<BackupOperationException>(
            () => fixture.Service.CreateAsync(
                new BackupCreationRequest(
                    UlidId.New(fixture.UtcNow),
                    "manual",
                    fixture.UtcNow)));

        Assert.True(exception.IsConfigurationError);
        Assert.Equal(
            "backup_destination_encryption_unconfirmed",
            exception.ErrorCode);
    }

    [Fact]
    public async Task ManifestEntryLimitFailsWithoutPublishingPartialSet()
    {
        await using var fixture = await BackupFixture.CreateAsync(
            maximumManifestEntries: 1);
        await fixture.AddObjectAsync(
            ContentStorageClass.TemplateSource,
            "template_source",
            managedScanBytes: false,
            "template-source");
        var backupId = UlidId.New(fixture.UtcNow);

        var exception = await Assert.ThrowsAsync<BackupOperationException>(
            () => fixture.Service.CreateAsync(
                new BackupCreationRequest(
                    backupId,
                    "manual",
                    fixture.UtcNow)));

        Assert.Equal("backup_manifest_entry_limit", exception.ErrorCode);
        Assert.False(Directory.Exists(Path.Combine(
            fixture.DestinationPath,
            "sets",
            fixture.UtcNow.ToString("yyyy", CultureInfo.InvariantCulture),
            fixture.UtcNow.ToString("MM", CultureInfo.InvariantCulture),
            backupId)));
    }

    [Fact]
    public async Task RetentionUsesDailyWeeklyAndMonthlyBuckets()
    {
        await using var fixture = await BackupFixture.CreateAsync(
            dailyRetentionDays: 14,
            weeklyRetentionWeeks: 4,
            monthlyRetentionMonths: 2);
        var newest = await fixture.CreateRetentionCandidateAsync(
            fixture.UtcNow.AddDays(-1));
        var daily = await fixture.CreateRetentionCandidateAsync(
            fixture.UtcNow.AddDays(-2));
        var weeklyNewest = await fixture.CreateRetentionCandidateAsync(
            fixture.UtcNow.AddDays(-20));
        var weeklyDuplicate = await fixture.CreateRetentionCandidateAsync(
            fixture.UtcNow.AddDays(-21));
        var monthlyNewest = await fixture.CreateRetentionCandidateAsync(
            fixture.UtcNow.AddDays(-40));
        var monthlyDuplicate = await fixture.CreateRetentionCandidateAsync(
            fixture.UtcNow.AddDays(-45));
        var beyondMonthly = await fixture.CreateRetentionCandidateAsync(
            fixture.UtcNow.AddDays(-100));

        var result = await fixture.Service.PruneAsync(
        [
            newest,
            daily,
            weeklyNewest,
            weeklyDuplicate,
            monthlyNewest,
            monthlyDuplicate,
            beyondMonthly,
        ]);

        Assert.Empty(result.Failures);
        Assert.Equal(
            [
                beyondMonthly.BackupId,
                monthlyDuplicate.BackupId,
                weeklyDuplicate.BackupId,
            ],
            result.ExpiredBackupIds);
        Assert.True(Directory.Exists(fixture.ResolveBackup(
            newest.DestinationRelativePath)));
        Assert.True(Directory.Exists(fixture.ResolveBackup(
            daily.DestinationRelativePath)));
        Assert.True(Directory.Exists(fixture.ResolveBackup(
            weeklyNewest.DestinationRelativePath)));
        Assert.True(Directory.Exists(fixture.ResolveBackup(
            monthlyNewest.DestinationRelativePath)));
        Assert.False(Directory.Exists(fixture.ResolveBackup(
            beyondMonthly.DestinationRelativePath)));
    }

    [Fact]
    public async Task ScanInclusiveBackupExpiresAfterSevenDaysEvenInsideDailyWindow()
    {
        await using var fixture = await BackupFixture.CreateAsync(
            includeManagedScans: true,
            dailyRetentionDays: 30);
        await fixture.AddObjectAsync(
            ContentStorageClass.ManagedScanOriginal,
            "submitted_scan",
            managedScanBytes: true,
            "student-handwriting");
        var oldScanBackup = await fixture.CreateRetentionCandidateAsync(
            fixture.UtcNow.AddDays(-7));
        var newestScanBackup = await fixture.CreateRetentionCandidateAsync(
            fixture.UtcNow.AddDays(-1));

        var result = await fixture.Service.PruneAsync(
        [
            oldScanBackup,
            newestScanBackup,
        ]);

        Assert.Empty(result.Failures);
        Assert.Equal(
            [oldScanBackup.BackupId],
            result.ExpiredBackupIds);
        Assert.False(Directory.Exists(fixture.ResolveBackup(
            oldScanBackup.DestinationRelativePath)));
        Assert.True(Directory.Exists(fixture.ResolveBackup(
            newestScanBackup.DestinationRelativePath)));
    }

    [Fact]
    public async Task RetentionStillRemovesExpiredSetsAfterBackupsAreDisabled()
    {
        await using var fixture = await BackupFixture.CreateAsync(
            dailyRetentionDays: 1,
            weeklyRetentionWeeks: 1,
            monthlyRetentionMonths: 1);
        var old = await fixture.CreateRetentionCandidateAsync(
            fixture.UtcNow.AddDays(-100));
        var newest = await fixture.CreateRetentionCandidateAsync(
            fixture.UtcNow.AddDays(-1));
        var cleanupService = new SqliteOnlineBackupArchiveService(
            new BackupOptions
            {
                DatabasePath = fixture.DatabasePath,
                ContentRootPath = fixture.ObjectPath,
                DestinationRootPath = fixture.DestinationPath,
                Enabled = false,
                DestinationEncryptionConfirmed = false,
                DailyRetentionDays = 1,
                WeeklyRetentionWeeks = 1,
                MonthlyRetentionMonths = 1,
                ApplicationVersion = "backup-retention-test",
            },
            fixture.ContentStore,
            new FixedTimeProvider(fixture.UtcNow));

        var result = await cleanupService.PruneAsync([old, newest]);

        Assert.Empty(result.Failures);
        Assert.Equal([old.BackupId], result.ExpiredBackupIds);
        Assert.False(Directory.Exists(fixture.ResolveBackup(
            old.DestinationRelativePath)));
        Assert.True(Directory.Exists(fixture.ResolveBackup(
            newest.DestinationRelativePath)));
    }

    [Fact]
    public async Task RetentionNeverDeletesNewestOrTamperedBackupSet()
    {
        await using var fixture = await BackupFixture.CreateAsync(
            dailyRetentionDays: 1,
            weeklyRetentionWeeks: 1,
            monthlyRetentionMonths: 1);
        var old = await fixture.CreateRetentionCandidateAsync(
            fixture.UtcNow.AddDays(-100));
        var newest = await fixture.CreateRetentionCandidateAsync(
            fixture.UtcNow.AddDays(-90));
        var manifestPath = Path.Combine(
            fixture.ResolveBackup(old.DestinationRelativePath),
            "manifest.json");
        await File.AppendAllTextAsync(manifestPath, "tampered");

        var result = await fixture.Service.PruneAsync([old, newest]);

        Assert.Empty(result.ExpiredBackupIds);
        Assert.Contains(
            result.Failures,
            failure => failure.BackupId == old.BackupId);
        Assert.True(Directory.Exists(fixture.ResolveBackup(
            old.DestinationRelativePath)));
        Assert.True(Directory.Exists(fixture.ResolveBackup(
            newest.DestinationRelativePath)));
    }

    [Fact]
    public async Task RetentionRejectsTraversalWithoutTouchingOutsideDirectory()
    {
        await using var fixture = await BackupFixture.CreateAsync(
            dailyRetentionDays: 1,
            weeklyRetentionWeeks: 1,
            monthlyRetentionMonths: 1);
        var newest = await fixture.CreateRetentionCandidateAsync(
            fixture.UtcNow.AddDays(-1));
        var maliciousId = UlidId.New(fixture.UtcNow.AddDays(-100));
        var outsidePath = Path.Combine(fixture.RootPath, "outside", maliciousId);
        Directory.CreateDirectory(outsidePath);
        var sentinelPath = Path.Combine(outsidePath, "sentinel.txt");
        await File.WriteAllTextAsync(sentinelPath, "must-remain");
        var malicious = new BackupRetentionCandidate(
            maliciousId,
            $"../outside/{maliciousId}",
            new string('a', 64),
            fixture.UtcNow.AddDays(-100));

        var result = await fixture.Service.PruneAsync([malicious, newest]);

        Assert.Empty(result.ExpiredBackupIds);
        Assert.Contains(
            result.Failures,
            failure => failure.BackupId == maliciousId);
        Assert.True(File.Exists(sentinelPath));
    }

    [Fact]
    public async Task RetentionRefusesBackupSetContainingAReparsePoint()
    {
        await using var fixture = await BackupFixture.CreateAsync(
            dailyRetentionDays: 1,
            weeklyRetentionWeeks: 1,
            monthlyRetentionMonths: 1);
        var old = await fixture.CreateRetentionCandidateAsync(
            fixture.UtcNow.AddDays(-100));
        var newest = await fixture.CreateRetentionCandidateAsync(
            fixture.UtcNow.AddDays(-1));
        var outsidePath = Path.Combine(fixture.RootPath, "outside-sentinel.txt");
        await File.WriteAllTextAsync(outsidePath, "must-remain");
        var linkPath = Path.Combine(
            fixture.ResolveBackup(old.DestinationRelativePath),
            "outside-link");
        try
        {
            File.CreateSymbolicLink(linkPath, outsidePath);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
        {
            return;
        }

        var result = await fixture.Service.PruneAsync([old, newest]);

        Assert.Empty(result.ExpiredBackupIds);
        Assert.Contains(
            result.Failures,
            failure => failure.BackupId == old.BackupId
                && failure.ErrorCode == "backup_retention_delete_failed");
        Assert.True(File.Exists(linkPath));
        Assert.True(File.Exists(outsidePath));
    }

    private sealed class BackupFixture : IAsyncDisposable
    {
        private int _nextIdOffset;

        private BackupFixture(
            string rootPath,
            string databasePath,
            string objectPath,
            string destinationPath,
            DateTimeOffset utcNow,
            DbContextOptions<OokiGraderDbContext> dbOptions,
            NtfsContentStore contentStore,
            SqliteOnlineBackupArchiveService service)
        {
            RootPath = rootPath;
            DatabasePath = databasePath;
            ObjectPath = objectPath;
            DestinationPath = destinationPath;
            UtcNow = utcNow;
            DbOptions = dbOptions;
            ContentStore = contentStore;
            Service = service;
        }

        public string RootPath { get; }
        public string DatabasePath { get; }
        public string ObjectPath { get; }
        public string DestinationPath { get; }
        public DateTimeOffset UtcNow { get; }
        public DbContextOptions<OokiGraderDbContext> DbOptions { get; }
        public NtfsContentStore ContentStore { get; }
        public SqliteOnlineBackupArchiveService Service { get; }

        public static async Task<BackupFixture> CreateAsync(
            bool encryptionConfirmed = true,
            int maximumManifestEntries = 1_000,
            bool includeManagedScans = false,
            int dailyRetentionDays = 14,
            int weeklyRetentionWeeks = 8,
            int monthlyRetentionMonths = 12)
        {
            var rootPath = Path.Combine(
                Path.GetTempPath(),
                "ooki-backup-tests",
                Guid.NewGuid().ToString("N"));
            var databasePath = Path.Combine(rootPath, "data", "ooki.db");
            var objectPath = Path.Combine(rootPath, "data", "objects");
            var destinationPath = Path.Combine(rootPath, "encrypted-backups");
            var secretPath = Path.Combine(rootPath, "data", "secrets");
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            Directory.CreateDirectory(secretPath);
            await File.WriteAllBytesAsync(
                Path.Combine(secretPath, "fixture.secret"),
                Encoding.UTF8.GetBytes("protected-envelope-only"));
            var options = new DbContextOptionsBuilder<OokiGraderDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    ForeignKeys = true,
                    Pooling = false,
                }.ToString())
                .AddInterceptors(new SqlitePragmaConnectionInterceptor())
                .Options;
            var utcNow = new DateTimeOffset(
                2026,
                7,
                27,
                2,
                30,
                0,
                TimeSpan.Zero);
            await using (var db = new OokiGraderDbContext(
                options,
                new FixedClock(utcNow)))
            {
                await db.Database.EnsureCreatedAsync();
                db.SiteSettings.Add(new SiteSettingsEntity
                {
                    Id = "site",
                    SchoolName = "Backup Test School",
                    DataRoot = Path.GetDirectoryName(databasePath)!,
                    CreatedAt = utcNow,
                    UpdatedAt = utcNow,
                });
                await db.SaveChangesAsync();
            }

            var store = new NtfsContentStore(new ContentStoreOptions
            {
                RootPath = objectPath,
            });
            var service = new SqliteOnlineBackupArchiveService(
                new BackupOptions
                {
                    DatabasePath = databasePath,
                    ContentRootPath = objectPath,
                    SecretEnvelopeRootPath = secretPath,
                    DestinationRootPath = destinationPath,
                    Enabled = true,
                    DestinationEncryptionConfirmed = encryptionConfirmed,
                    IncludeManagedScans = includeManagedScans,
                    DailyRetentionDays = dailyRetentionDays,
                    WeeklyRetentionWeeks = weeklyRetentionWeeks,
                    MonthlyRetentionMonths = monthlyRetentionMonths,
                    MaximumManifestEntries = maximumManifestEntries,
                    ApplicationVersion = "backup-test",
                },
                store,
                new FixedTimeProvider(utcNow));
            return new BackupFixture(
                rootPath,
                databasePath,
                objectPath,
                destinationPath,
                utcNow,
                options,
                store,
                service);
        }

        public async Task<BackupRetentionCandidate>
            CreateRetentionCandidateAsync(DateTimeOffset completedAt)
        {
            var backupId = UlidId.New(completedAt);
            var result = await Service.CreateAsync(
                new BackupCreationRequest(
                    backupId,
                    "scheduled",
                    completedAt));
            return new BackupRetentionCandidate(
                backupId,
                result.DestinationRelativePath,
                result.ManifestSha256,
                completedAt);
        }

        public async Task<SeededFile> AddObjectAsync(
            ContentStorageClass storageClass,
            string retentionClass,
            bool managedScanBytes,
            string content)
        {
            await using var source = new MemoryStream(
                Encoding.UTF8.GetBytes(content));
            var stored = await ContentStore.PutAsync(
                source,
                storageClass,
                "bin");
            var fileObjectId = UlidId.New(
                UtcNow.AddMilliseconds(
                    Interlocked.Increment(ref _nextIdOffset)));
            await using var db = new OokiGraderDbContext(
                DbOptions,
                new FixedClock(UtcNow));
            db.FileObjects.Add(new FileObjectEntity
            {
                Id = fileObjectId,
                Sha256 = stored.Locator.Sha256,
                Bytes = stored.Locator.Bytes,
                VerifiedMime = "application/octet-stream",
                Extension = stored.Locator.Extension,
                RelativeObjectPath = stored.RelativePath,
                StorageClass = storageClass.ToString(),
                RetentionClass = retentionClass,
                ManagedScanBytes = managedScanBytes,
                State = "available",
                CreatedAt = UtcNow,
                VerifiedAt = UtcNow,
            });
            await db.SaveChangesAsync();
            return new SeededFile(fileObjectId, stored.Locator);
        }

        public async Task<SqliteConnection> OpenWalWriterAsync()
        {
            var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = DatabasePath,
                    Mode = SqliteOpenMode.ReadWrite,
                    ForeignKeys = true,
                    Pooling = false,
                }.ToString());
            await connection.OpenAsync();
            await using var pragmas = connection.CreateCommand();
            pragmas.CommandText =
                "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;";
            await pragmas.ExecuteNonQueryAsync();
            return connection;
        }

        public string ResolveBackup(string relativePath) =>
            Path.Combine(
                DestinationPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar));

        public ValueTask DisposeAsync()
        {
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

            return ValueTask.CompletedTask;
        }
    }

    private sealed record SeededFile(
        string FileObjectId,
        ContentObjectLocator Locator);

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private static JsonSerializerOptions JsonOptions { get; } =
        new(JsonSerializerDefaults.Web);
}
