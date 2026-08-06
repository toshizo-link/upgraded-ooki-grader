using System.Text.Json;
using System.Reflection;
using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Infrastructure.Backups;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Infrastructure.Storage;
using OokiGrader.Tool;

namespace OokiGrader.Tool.Tests;

public sealed class ToolApplicationTests
{
    [Fact]
    public async Task HealthUsesReadOnlyDatabaseAndRedactsLocalPathsAndSchoolData()
    {
        await using var fixture = await ToolFixture.CreateAsync();
        var beforeDatabase = Snapshot(fixture.DatabasePath);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await ToolApplication.RunAsync(
            [
                "health",
                "--database",
                fixture.DatabasePath,
                "--data-root",
                fixture.LiveRoot,
                "--content-root",
                fixture.ContentRoot,
                "--json",
            ],
            output,
            error);

        Assert.Equal(ToolExitCodes.Success, exitCode);
        Assert.Empty(error.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(
            "healthy",
            json.RootElement.GetProperty("state").GetString());
        Assert.False(
            json.RootElement.GetProperty("mutationPerformed").GetBoolean());
        Assert.False(
            json.RootElement
                .GetProperty("storage")
                .GetProperty("writeProbePerformed")
                .GetBoolean());
        Assert.True(
            json.RootElement
                .GetProperty("database")
                .GetProperty("schemaCurrent")
                .GetBoolean());
        Assert.DoesNotContain(
            fixture.LiveRoot,
            output.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Private Student School",
            output.ToString(),
            StringComparison.Ordinal);
        Assert.Equal(beforeDatabase, Snapshot(fixture.DatabasePath));
    }

    [Fact]
    public async Task BackupVerifyUsesArchiveServiceWithoutWriteProbe()
    {
        await using var fixture = await ToolFixture.CreateAsync();
        var backup = await fixture.CreateBackupAsync();
        var beforeEntries = SnapshotEntries(fixture.BackupRoot);
        var createdEntries = new ConcurrentQueue<string>();
        using var watcher = new FileSystemWatcher(fixture.BackupRoot)
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
        };
        watcher.Created += (_, eventArgs) =>
            createdEntries.Enqueue(eventArgs.FullPath);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await ToolApplication.RunAsync(
            fixture.BackupArguments(
                "backup",
                "verify",
                backup,
                json: true),
            output,
            error);

        Assert.Equal(ToolExitCodes.Success, exitCode);
        Assert.Empty(error.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        Assert.True(
            json.RootElement.GetProperty("verified").GetBoolean());
        Assert.False(
            json.RootElement.GetProperty("mutationPerformed").GetBoolean());
        Assert.Equal(beforeEntries, SnapshotEntries(fixture.BackupRoot));
        await Task.Delay(100);
        Assert.Empty(createdEntries);
        Assert.DoesNotContain(
            ".write-probe-",
            Directory.EnumerateFiles(
                fixture.BackupRoot,
                "*",
                SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .Where(name => name is not null));
    }

    [Fact]
    public async Task RestorePlanNeverOffersLiveDataMutation()
    {
        await using var fixture = await ToolFixture.CreateAsync();
        var backup = await fixture.CreateBackupAsync();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await ToolApplication.RunAsync(
            fixture.BackupArguments(
                "restore",
                "plan",
                backup,
                json: true),
            output,
            error);

        Assert.Equal(ToolExitCodes.Success, exitCode);
        Assert.Empty(error.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(
            "validation-and-plan-only",
            json.RootElement.GetProperty("operationMode").GetString());
        Assert.False(
            json.RootElement
                .GetProperty("liveDataOverwriteSupported")
                .GetBoolean());
        Assert.True(
            json.RootElement
                .GetProperty("maintenanceConfirmationRequired")
                .GetBoolean());
        Assert.True(
            json.RootElement
                .GetProperty("offlineConfirmationRequired")
                .GetBoolean());
        Assert.False(
            json.RootElement.GetProperty("mutationPerformed").GetBoolean());
    }

    [Fact]
    public async Task RestoreExecuteStagesVerifiedBackupAndPreservesRollback()
    {
        await using var fixture = await ToolFixture.CreateAsync();
        var backup = await fixture.CreateBackupAsync();
        var backupEntries = SnapshotEntries(fixture.BackupRoot);
        await fixture.PrepareChangedMaintenanceStateAsync();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await ToolApplication.RunAsync(
            fixture.RestoreExecuteArguments(backup),
            output,
            error);

        Assert.Equal(ToolExitCodes.Success, exitCode);
        Assert.Empty(error.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(
            "restored-awaiting-signoff",
            json.RootElement.GetProperty("state").GetString());
        Assert.True(
            json.RootElement.GetProperty("mutationPerformed").GetBoolean());
        Assert.True(
            json.RootElement
                .GetProperty("rollbackSnapshotCreated")
                .GetBoolean());
        Assert.True(
            json.RootElement
                .GetProperty("maintenanceModeEnforced")
                .GetBoolean());
        Assert.True(
            json.RootElement
                .GetProperty("restoreMarkerPresent")
                .GetBoolean());
        Assert.DoesNotContain(
            fixture.LiveRoot,
            output.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Private Student School",
            output.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Current State School",
            output.ToString(),
            StringComparison.Ordinal);

        var restored = await ReadDatabaseStateAsync(fixture.DatabasePath);
        Assert.Equal("Private Student School", restored.SchoolName);
        Assert.True(restored.MaintenanceMode);
        Assert.Equal(1, restored.RestoreAuditCount);
        Assert.True(File.Exists(Path.Combine(
            fixture.LiveRoot,
            "operations",
            "restore.in-progress")));
        Assert.Equal(
            "backup-secret-envelope",
            await File.ReadAllTextAsync(Path.Combine(
                fixture.LiveRoot,
                "secrets",
                "provider.secret")));

        var rollbackRoot = fixture.RollbackRoot(backup.BackupId);
        Assert.True(Directory.Exists(rollbackRoot));
        var rollback = await ReadDatabaseStateAsync(Path.Combine(
            rollbackRoot,
            "ooki-grader.db"));
        Assert.Equal("Current State School", rollback.SchoolName);
        Assert.True(rollback.MaintenanceMode);
        Assert.True(File.Exists(Path.Combine(
            rollbackRoot,
            "current-only.txt")));
        Assert.Equal(
            "changed-secret-envelope",
            await File.ReadAllTextAsync(Path.Combine(
                rollbackRoot,
                "secrets",
                "provider.secret")));
        Assert.Equal(backupEntries, SnapshotEntries(fixture.BackupRoot));
    }

    [Fact]
    public async Task RestoreExecuteRefusesDatabaseOutsideMaintenanceWithoutMutation()
    {
        await using var fixture = await ToolFixture.CreateAsync();
        var backup = await fixture.CreateBackupAsync();
        var beforeDatabase = Snapshot(fixture.DatabasePath);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await ToolApplication.RunAsync(
            fixture.RestoreExecuteArguments(backup),
            output,
            error);

        Assert.Equal(ToolExitCodes.SafetyRefusal, exitCode);
        Assert.Empty(output.ToString());
        using var json = JsonDocument.Parse(error.ToString());
        Assert.Equal(
            "restore_maintenance_mode_required",
            json.RootElement.GetProperty("errorCode").GetString());
        Assert.False(
            json.RootElement.GetProperty("mutationPerformed").GetBoolean());
        Assert.Equal(beforeDatabase, Snapshot(fixture.DatabasePath));
        Assert.False(Directory.Exists(fixture.RollbackRoot(backup.BackupId)));
        Assert.False(Directory.Exists(fixture.StagingRoot(backup.BackupId)));
        Assert.False(File.Exists(Path.Combine(
            fixture.LiveRoot,
            "operations",
            "restore.in-progress")));
    }

    [Fact]
    public async Task RestoreExecuteRequiresExactTypedBackupIdentifier()
    {
        await using var fixture = await ToolFixture.CreateAsync();
        var backup = await fixture.CreateBackupAsync();
        await fixture.PrepareChangedMaintenanceStateAsync();
        var arguments = fixture.RestoreExecuteArguments(backup);
        arguments[Array.IndexOf(arguments, "--confirm-restore") + 1] =
            "01ARZ3NDEKTSV4RRFFQ69G5FAV";
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await ToolApplication.RunAsync(
            arguments,
            output,
            error);

        Assert.Equal(ToolExitCodes.SafetyRefusal, exitCode);
        using var json = JsonDocument.Parse(error.ToString());
        Assert.Equal(
            "restore_destructive_confirmation_mismatch",
            json.RootElement.GetProperty("errorCode").GetString());
        Assert.False(
            json.RootElement.GetProperty("mutationPerformed").GetBoolean());
        Assert.False(Directory.Exists(fixture.RollbackRoot(backup.BackupId)));
        Assert.False(Directory.Exists(fixture.StagingRoot(backup.BackupId)));
    }

    [Fact]
    public void RestoreDirectorySwitchFailureRestoresOriginalLiveRoot()
    {
        var operations = new FailingDirectoryOperations(
            "staging",
            "live",
            "rollback");

        var exception = Assert.Throws<RestoreExecutionException>(() =>
            RestoreDirectorySwitcher.Switch(
                "staging",
                "live",
                "rollback",
                operations));

        Assert.Equal(
            "restore_switch_failed_rolled_back",
            exception.ErrorCode);
        Assert.True(exception.MutationPerformed);
        Assert.Contains("live", operations.Directories);
        Assert.Contains("staging", operations.Directories);
        Assert.DoesNotContain("rollback", operations.Directories);
    }

    [Fact]
    public async Task BackupDiagnosticsRequireExplicitEncryptionConfirmation()
    {
        await using var fixture = await ToolFixture.CreateAsync();
        var backup = await fixture.CreateBackupAsync();
        var arguments = fixture.BackupArguments(
                "backup",
                "verify",
                backup,
                json: true)
            .Where(argument =>
                argument != "--destination-encryption-confirmed")
            .ToArray();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await ToolApplication.RunAsync(
            arguments,
            output,
            error);

        Assert.Equal(ToolExitCodes.Usage, exitCode);
        using var json = JsonDocument.Parse(error.ToString());
        Assert.Equal(
            "destination_encryption_confirmation_required",
            json.RootElement.GetProperty("errorCode").GetString());
        Assert.False(
            json.RootElement.GetProperty("mutationPerformed").GetBoolean());
    }

    [Fact]
    public async Task RestoreApplyIsRejectedWithoutEchoingItsValue()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await ToolApplication.RunAsync(
            ["restore", "plan", "--apply", "sensitive-value", "--json"],
            output,
            error);

        Assert.Equal(ToolExitCodes.Usage, exitCode);
        Assert.DoesNotContain(
            "sensitive-value",
            error.ToString(),
            StringComparison.Ordinal);
        using var json = JsonDocument.Parse(error.ToString());
        Assert.Equal(
            "option_unknown",
            json.RootElement.GetProperty("errorCode").GetString());
    }

    private static FileSnapshot Snapshot(string path)
    {
        var info = new FileInfo(path);
        return new FileSnapshot(info.Length, info.LastWriteTimeUtc);
    }

    private static string[] SnapshotEntries(string path) =>
        Directory.EnumerateFileSystemEntries(
            path,
            "*",
            SearchOption.AllDirectories)
            .Select(entry => Path.GetRelativePath(path, entry))
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToArray();

    private sealed record FileSnapshot(long Bytes, DateTime LastWriteAt);

    private sealed record DatabaseState(
        string SchoolName,
        bool MaintenanceMode,
        long RestoreAuditCount);

    private static async Task<DatabaseState> ReadDatabaseStateAsync(
        string path)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var settings = connection.CreateCommand();
        settings.CommandText =
            """
            SELECT school_name, maintenance_mode
            FROM site_settings
            WHERE id = 'site';
            """;
        await using var reader = await settings.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var schoolName = reader.GetString(0);
        var maintenanceMode = reader.GetBoolean(1);
        await reader.DisposeAsync();
        await using var audit = connection.CreateCommand();
        audit.CommandText =
            """
            SELECT COUNT(*)
            FROM audit_event
            WHERE event_type = 'restore.executed';
            """;
        var restoreAuditCount = (long)(await audit.ExecuteScalarAsync())!;
        return new DatabaseState(
            schoolName,
            maintenanceMode,
            restoreAuditCount);
    }

    private sealed class ToolFixture : IAsyncDisposable
    {
        private ToolFixture(
            string root,
            string liveRoot,
            string contentRoot,
            string secretRoot,
            string backupRoot,
            string databasePath,
            TestClock clock)
        {
            Root = root;
            LiveRoot = liveRoot;
            ContentRoot = contentRoot;
            SecretRoot = secretRoot;
            BackupRoot = backupRoot;
            DatabasePath = databasePath;
            Clock = clock;
        }

        public string Root { get; }

        public string LiveRoot { get; }

        public string ContentRoot { get; }

        public string SecretRoot { get; }

        public string BackupRoot { get; }

        public string DatabasePath { get; }

        public TestClock Clock { get; }

        public static async Task<ToolFixture> CreateAsync()
        {
            var root = Path.Combine(
                CanonicalTempPath(),
                "ooki-grader-tool-tests",
                Guid.NewGuid().ToString("N"));
            var liveRoot = Path.Combine(root, "live");
            var contentRoot = Path.Combine(liveRoot, "objects");
            var secretRoot = Path.Combine(liveRoot, "secrets");
            var backupRoot = Path.Combine(root, "backups");
            Directory.CreateDirectory(contentRoot);
            Directory.CreateDirectory(secretRoot);
            Directory.CreateDirectory(backupRoot);
            await File.WriteAllTextAsync(
                Path.Combine(secretRoot, "provider.secret"),
                "backup-secret-envelope");
            var databasePath = Path.Combine(liveRoot, "ooki-grader.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                ForeignKeys = true,
                Pooling = false,
            }.ToString();
            var options = new DbContextOptionsBuilder<OokiGraderDbContext>()
                .UseSqlite(connectionString)
                .Options;
            var clock = new TestClock(new DateTimeOffset(
                2026,
                7,
                27,
                8,
                0,
                0,
                TimeSpan.Zero));
            await using var database = new OokiGraderDbContext(options, clock);
            await database.Database.EnsureCreatedAsync();
            database.SiteSettings.Add(new SiteSettingsEntity
            {
                Id = "site",
                SchoolName = "Private Student School",
                TimeZone = "Asia/Tokyo",
                Locale = "ja-JP",
                DataRoot = liveRoot,
                PhysicalFreeReserveBytes = 1,
                CreatedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow,
            });
            await database.SaveChangesAsync();
            var migrationId = typeof(OokiGraderDbContext).Assembly
                .GetTypes()
                .Select(type =>
                    type.GetCustomAttribute<MigrationAttribute>()?.Id)
                .Where(id => id is not null)
                .OrderBy(id => id, StringComparer.Ordinal)
                .Last()!;
            await database.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE "__EFMigrationsHistory" (
                    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                    "ProductVersion" TEXT NOT NULL
                );
                """);
            await database.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                VALUES ({migrationId}, '10.0.10');
                """);
            return new ToolFixture(
                root,
                liveRoot,
                contentRoot,
                secretRoot,
                backupRoot,
                databasePath,
                clock);
        }

        private static string CanonicalTempPath()
        {
            var tempPath = Path.GetTempPath();
            return OperatingSystem.IsMacOS()
                && tempPath.StartsWith(
                    "/var/",
                    StringComparison.Ordinal)
                    ? $"/private{tempPath}"
                    : tempPath;
        }

        public async Task<BackupCreationResult> CreateBackupAsync()
        {
            var service = new SqliteOnlineBackupArchiveService(
                new BackupOptions
                {
                    DatabasePath = DatabasePath,
                    ContentRootPath = ContentRoot,
                    SecretEnvelopeRootPath = SecretRoot,
                    DestinationRootPath = BackupRoot,
                    DestinationEncryptionConfirmed = true,
                    Enabled = true,
                },
                new NtfsContentStore(new ContentStoreOptions
                {
                    RootPath = ContentRoot,
                }),
                new TestTimeProvider(Clock.UtcNow));
            var backupId = UlidId.New(Clock.UtcNow);
            return await service.CreateAsync(
                new BackupCreationRequest(
                    backupId,
                    "manual",
                    Clock.UtcNow));
        }

        public async Task PrepareChangedMaintenanceStateAsync()
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE site_settings
                SET school_name = 'Current State School',
                    maintenance_mode = 1,
                    revision = revision + 1;
                """;
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
            await File.WriteAllTextAsync(
                Path.Combine(SecretRoot, "provider.secret"),
                "changed-secret-envelope");
            await File.WriteAllTextAsync(
                Path.Combine(LiveRoot, "current-only.txt"),
                "rollback evidence");
        }

        public string[] RestoreExecuteArguments(BackupCreationResult backup)
        {
            var arguments = BackupArguments(
                    "restore",
                    "execute",
                    backup,
                    json: true)
                .ToList();
            arguments.AddRange(
            [
                "--data-root",
                LiveRoot,
                "--maintenance-confirmed",
                "--offline-confirmed",
                "--confirm-restore",
                backup.BackupId,
            ]);
            return arguments.ToArray();
        }

        public string RollbackRoot(string backupId) =>
            Path.Combine(Root, $"live.rollback-{backupId}");

        public string StagingRoot(string backupId) =>
            Path.Combine(Root, $".live.restore-staging-{backupId}");

        public string[] BackupArguments(
            string command,
            string subcommand,
            BackupCreationResult backup,
            bool json)
        {
            var arguments = new List<string>
            {
                command,
                subcommand,
                "--database",
                DatabasePath,
                "--content-root",
                ContentRoot,
                "--destination",
                BackupRoot,
                "--destination-encryption-confirmed",
                "--backup-id",
                backup.BackupId,
                "--relative-path",
                backup.DestinationRelativePath,
                "--manifest-sha256",
                backup.ManifestSha256,
            };
            if (json)
            {
                arguments.Add("--json");
            }

            return arguments.ToArray();
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingDirectoryOperations(
        string staging,
        string live,
        string rollback) : IRestoreDirectoryOperations
    {
        public HashSet<string> Directories { get; } =
        [
            staging,
            live,
        ];

        public bool DirectoryExists(string path) =>
            Directories.Contains(path);

        public void MoveDirectory(string source, string destination)
        {
            if (source == staging && destination == live)
            {
                throw new IOException("Synthetic activation failure.");
            }

            Assert.True(Directories.Remove(source));
            Assert.True(Directories.Add(destination));
            if (source == live)
            {
                Assert.Equal(rollback, destination);
            }
        }
    }

    private sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class TestTimeProvider(
        DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
