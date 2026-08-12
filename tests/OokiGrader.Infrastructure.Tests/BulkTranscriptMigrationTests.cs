using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OokiGrader.Application.Identifiers;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Infrastructure.Tests;

public sealed class BulkTranscriptMigrationTests
{
    private const string Migration0019 =
        "20260810120000_0019_QuestionGradingFlags";
    private const string Migration0020 =
        "20260810180000_0020_BulkTranscriptExports";
    private const string Migration0021 =
        "20260810190000_0021_BulkTranscriptExportHardening";

    [Fact]
    public async Task Migration0020IsAdditiveTriggerSafeAndRoundTripsFrom0019()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ooki-bulk-migration-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new DbContextOptionsBuilder<OokiGraderDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(root, "upgrade.db"),
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    ForeignKeys = true,
                    DefaultTimeout = 5,
                    Pooling = false,
                }.ToString())
                .AddInterceptors(new SqlitePragmaConnectionInterceptor())
                .Options;
            await using var context = new OokiGraderDbContext(options);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(Migration0019);
            await context.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER trg_0020_submission_sentinel
                AFTER UPDATE ON submission BEGIN SELECT 1; END;
                CREATE TRIGGER trg_0020_export_sentinel
                AFTER UPDATE ON export_record BEGIN SELECT 1; END;
                """);
            await context.Database.OpenConnectionAsync();
            var rootsBefore = await LoadRootPagesAsync(
                context,
                "submission",
                "template_version",
                "background_job",
                "export_record");
            await context.Database.CloseConnectionAsync();

            var script = migrator.GenerateScript(Migration0019, Migration0020);
            Assert.Contains(
                "CREATE TABLE \"bulk_transcript_export\"",
                script,
                StringComparison.Ordinal);
            Assert.DoesNotContain("ALTER TABLE", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DROP TABLE", script, StringComparison.OrdinalIgnoreCase);

            await migrator.MigrateAsync(Migration0020);
            await context.Database.OpenConnectionAsync();
            var rootsAfterUpgrade = await LoadRootPagesAsync(
                context,
                "submission",
                "template_version",
                "background_job",
                "export_record");
            Assert.Equal(rootsBefore, rootsAfterUpgrade);
            Assert.Equal(
                2L,
                Convert.ToInt64(
                    await ScalarAsync(
                        context,
                        "SELECT COUNT(*) FROM sqlite_master " +
                        "WHERE type='trigger' AND name IN " +
                        "('trg_0020_submission_sentinel'," +
                        "'trg_0020_export_sentinel');"),
                    CultureInfo.InvariantCulture));
            var tableSql = (await ScalarAsync(
                context,
                "SELECT sql FROM sqlite_master WHERE type='table' " +
                "AND name='bulk_transcript_export';"))?.ToString();
            Assert.Contains(
                "student_count <= result_count",
                tableSql,
                StringComparison.Ordinal);
            Assert.Contains(
                "processed_result_count <= result_count",
                tableSql,
                StringComparison.Ordinal);
            await context.Database.CloseConnectionAsync();

            var now = DateTimeOffset.UtcNow;
            var staffId = UlidId.New(now);
            var jobId = UlidId.New(now.AddMilliseconds(1));
            context.StaffUsers.Add(new StaffUserEntity
            {
                Id = staffId,
                Username = "bulk.migration",
                UsernameNormalized = "bulk.migration",
                DisplayName = "移行テスト",
                PasswordHash = "test",
                PasswordAlgorithm = "test",
                PasswordAlgorithmVersion = 1,
                Status = "active",
                CredentialChangedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
            context.BackgroundJobs.Add(new BackgroundJobEntity
            {
                Id = jobId,
                Type = "bulk_transcript_export.render",
                SchemaVersion = 1,
                DeduplicationKey = "bulk-migration-check",
                PayloadJson = "{}",
                State = "queued",
                MaxAttempts = 1,
                NextAttemptAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await context.SaveChangesAsync();
            await Assert.ThrowsAsync<SqliteException>(() =>
                context.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO bulk_transcript_export (
                        id, background_job_id, selector_json, selector_hash,
                        source_snapshot_json, source_fingerprint,
                        renderer_version, package_format_version, state,
                        student_count, result_count, processed_result_count,
                        created_by_staff_user_id, created_at, updated_at, revision)
                    VALUES (
                        '01K13BULKINVALIDCOUNT001', @job, '{{}}',
                        'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                        '[]',
                        'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                        'renderer', 'zip-v1', 'queued', 2, 1, 0,
                        @staff, 1, 1, 1);
                    """,
                    new SqliteParameter("@job", jobId),
                    new SqliteParameter("@staff", staffId)));

            await migrator.MigrateAsync(Migration0019);
            await context.Database.OpenConnectionAsync();
            Assert.Equal(
                0L,
                Convert.ToInt64(
                    await ScalarAsync(
                        context,
                        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' " +
                        "AND name='bulk_transcript_export';"),
                    CultureInfo.InvariantCulture));
            Assert.Equal(
                rootsBefore,
                await LoadRootPagesAsync(
                    context,
                    "submission",
                    "template_version",
                    "background_job",
                    "export_record"));
            Assert.Equal(
                2L,
                Convert.ToInt64(
                    await ScalarAsync(
                        context,
                        "SELECT COUNT(*) FROM sqlite_master " +
                        "WHERE type='trigger' AND name IN " +
                        "('trg_0020_submission_sentinel'," +
                        "'trg_0020_export_sentinel');"),
                    CultureInfo.InvariantCulture));
            await context.Database.CloseConnectionAsync();

            await migrator.MigrateAsync(Migration0020);
            await context.Database.OpenConnectionAsync();
            Assert.Equal(
                1L,
                Convert.ToInt64(
                    await ScalarAsync(
                        context,
                        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' " +
                        "AND name='bulk_transcript_export';"),
                    CultureInfo.InvariantCulture));
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
            await context.Database.CloseConnectionAsync();

            Assert.Contains(
                Migration0020,
                await context.Database.GetAppliedMigrationsAsync());
            Assert.False(context.Database.HasPendingModelChanges());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Migration0021IsAdditiveAndRoundTripsWithoutLosingBulkExports()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ooki-bulk-migration-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new DbContextOptionsBuilder<OokiGraderDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(root, "hardening.db"),
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    ForeignKeys = true,
                    DefaultTimeout = 5,
                    Pooling = false,
                }.ToString())
                .AddInterceptors(new SqlitePragmaConnectionInterceptor())
                .Options;
            await using var context = new OokiGraderDbContext(options);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(Migration0020);

            await context.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER trg_0021_submission_sentinel
                AFTER UPDATE ON submission BEGIN SELECT 1; END;
                CREATE TRIGGER trg_0021_export_sentinel
                AFTER UPDATE ON export_record BEGIN SELECT 1; END;
                """);

            var now = DateTimeOffset.UtcNow;
            var firstStaffId = UlidId.New(now);
            var secondStaffId = UlidId.New(now.AddMilliseconds(1));
            context.StaffUsers.AddRange(
                new StaffUserEntity
                {
                    Id = firstStaffId,
                    Username = "bulk.hardening.first",
                    UsernameNormalized = "bulk.hardening.first",
                    DisplayName = "移行テスト1",
                    PasswordHash = "test",
                    PasswordAlgorithm = "test",
                    PasswordAlgorithmVersion = 1,
                    Status = "active",
                    CredentialChangedAt = now,
                    CreatedAt = now,
                    UpdatedAt = now,
                },
                new StaffUserEntity
                {
                    Id = secondStaffId,
                    Username = "bulk.hardening.second",
                    UsernameNormalized = "bulk.hardening.second",
                    DisplayName = "移行テスト2",
                    PasswordHash = "test",
                    PasswordAlgorithm = "test",
                    PasswordAlgorithmVersion = 1,
                    Status = "active",
                    CredentialChangedAt = now,
                    CreatedAt = now,
                    UpdatedAt = now,
                });

            var legacyJobId = UlidId.New(now.AddMilliseconds(2));
            var identifiedJobId = UlidId.New(now.AddMilliseconds(3));
            var duplicateJobId = UlidId.New(now.AddMilliseconds(4));
            var otherActorJobId = UlidId.New(now.AddMilliseconds(5));
            context.BackgroundJobs.AddRange(
                CreateBackgroundJob(legacyJobId, "bulk-hardening-legacy", now),
                CreateBackgroundJob(identifiedJobId, "bulk-hardening-identified", now),
                CreateBackgroundJob(duplicateJobId, "bulk-hardening-duplicate", now),
                CreateBackgroundJob(otherActorJobId, "bulk-hardening-other-actor", now));
            await context.SaveChangesAsync();

            var legacyExportId = UlidId.New(now.AddMilliseconds(6));
            await InsertLegacyBulkExportAsync(
                context,
                legacyExportId,
                legacyJobId,
                firstStaffId,
                createdAt: 1);

            await context.Database.OpenConnectionAsync();
            var rootsBefore = await LoadRootPagesAsync(
                context,
                "submission",
                "template_version",
                "background_job",
                "export_record",
                "bulk_transcript_export");
            await context.Database.CloseConnectionAsync();

            var script = migrator.GenerateScript(Migration0020, Migration0021);
            Assert.Equal(
                2,
                CountOccurrences(
                    script,
                    "ALTER TABLE \"bulk_transcript_export\" ADD "));
            Assert.Contains(
                "ADD \"request_fingerprint\" TEXT NULL",
                script,
                StringComparison.Ordinal);
            Assert.Contains(
                "ADD \"request_idempotency_key\" TEXT NULL",
                script,
                StringComparison.Ordinal);
            Assert.Contains(
                "CREATE UNIQUE INDEX \"IX_bulk_transcript_export_" +
                "created_by_staff_user_id_request_idempotency_key\"",
                script,
                StringComparison.Ordinal);
            Assert.Contains(
                "CREATE INDEX \"IX_bulk_transcript_export_" +
                "created_by_staff_user_id_state\"",
                script,
                StringComparison.Ordinal);
            Assert.DoesNotContain("DROP TABLE", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "CREATE TABLE \"bulk_transcript_export\"",
                script,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "CREATE TABLE \"ef_temp_bulk_transcript_export\"",
                script,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "INSERT INTO \"bulk_transcript_export\"",
                script,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("RENAME TO", script, StringComparison.OrdinalIgnoreCase);

            await migrator.MigrateAsync(Migration0021);
            await context.Database.OpenConnectionAsync();
            Assert.Equal(
                rootsBefore,
                await LoadRootPagesAsync(
                    context,
                    "submission",
                    "template_version",
                    "background_job",
                    "export_record",
                    "bulk_transcript_export"));
            await AssertSentinelTriggersExistAsync(context);
            Assert.Equal(
                2L,
                Convert.ToInt64(
                    await ScalarAsync(
                        context,
                        "SELECT COUNT(*) FROM pragma_table_info(" +
                        "'bulk_transcript_export') WHERE name IN " +
                        "('request_fingerprint','request_idempotency_key');"),
                    CultureInfo.InvariantCulture));
            Assert.Equal(
                2L,
                Convert.ToInt64(
                    await ScalarAsync(
                        context,
                        "SELECT COUNT(*) FROM sqlite_master WHERE type='index' " +
                        "AND name IN (" +
                        "'IX_bulk_transcript_export_created_by_staff_user_id_" +
                        "request_idempotency_key'," +
                        "'IX_bulk_transcript_export_created_by_staff_user_id_state');"),
                    CultureInfo.InvariantCulture));
            Assert.Equal(
                1L,
                Convert.ToInt64(
                    await ScalarAsync(
                        context,
                        "SELECT COUNT(*) FROM bulk_transcript_export " +
                        "WHERE request_idempotency_key IS NULL " +
                        "AND request_fingerprint IS NULL;"),
                    CultureInfo.InvariantCulture));
            var uniqueIndexSql = (await ScalarAsync(
                context,
                "SELECT sql FROM sqlite_master WHERE type='index' AND name=" +
                "'IX_bulk_transcript_export_created_by_staff_user_id_" +
                "request_idempotency_key';"))?.ToString();
            Assert.Contains("CREATE UNIQUE INDEX", uniqueIndexSql, StringComparison.Ordinal);
            Assert.Contains(
                "WHERE \"request_idempotency_key\" IS NOT NULL",
                uniqueIndexSql,
                StringComparison.Ordinal);
            await AssertDatabaseHealthyAsync(context);
            await context.Database.CloseConnectionAsync();

            const string requestKey = "migration-idempotency-key";
            const string requestFingerprint =
                "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
            var identifiedExportId = UlidId.New(now.AddMilliseconds(7));
            await InsertHardenedBulkExportAsync(
                context,
                identifiedExportId,
                identifiedJobId,
                firstStaffId,
                requestKey,
                requestFingerprint,
                createdAt: 2);
            var duplicateExportId = UlidId.New(now.AddMilliseconds(8));
            var duplicate = await Assert.ThrowsAsync<SqliteException>(() =>
                InsertHardenedBulkExportAsync(
                    context,
                    duplicateExportId,
                    duplicateJobId,
                    firstStaffId,
                    requestKey,
                    requestFingerprint,
                    createdAt: 3));
            Assert.Equal(19, duplicate.SqliteErrorCode);
            var otherActorExportId = UlidId.New(now.AddMilliseconds(9));
            await InsertHardenedBulkExportAsync(
                context,
                otherActorExportId,
                otherActorJobId,
                secondStaffId,
                requestKey,
                requestFingerprint,
                createdAt: 4);

            await migrator.MigrateAsync(Migration0020);
            await context.Database.OpenConnectionAsync();
            Assert.Equal(
                3L,
                Convert.ToInt64(
                    await ScalarAsync(
                        context,
                        "SELECT COUNT(*) FROM bulk_transcript_export;"),
                    CultureInfo.InvariantCulture));
            await AssertBulkExportsExistAsync(
                context,
                legacyExportId,
                identifiedExportId,
                otherActorExportId);
            Assert.Equal(
                0L,
                Convert.ToInt64(
                    await ScalarAsync(
                        context,
                        "SELECT COUNT(*) FROM pragma_table_info(" +
                        "'bulk_transcript_export') WHERE name IN " +
                        "('request_fingerprint','request_idempotency_key');"),
                    CultureInfo.InvariantCulture));
            Assert.Equal(
                rootsBefore
                    .Where(pair => pair.Key != "bulk_transcript_export")
                    .ToDictionary(pair => pair.Key, pair => pair.Value),
                await LoadRootPagesAsync(
                    context,
                    "submission",
                    "template_version",
                    "background_job",
                    "export_record"));
            await AssertSentinelTriggersExistAsync(context);
            await AssertDatabaseHealthyAsync(context);
            await context.Database.CloseConnectionAsync();
            Assert.DoesNotContain(
                Migration0021,
                await context.Database.GetAppliedMigrationsAsync());

            await migrator.MigrateAsync(Migration0021);
            await context.Database.OpenConnectionAsync();
            Assert.Equal(
                3L,
                Convert.ToInt64(
                    await ScalarAsync(
                        context,
                        "SELECT COUNT(*) FROM bulk_transcript_export;"),
                    CultureInfo.InvariantCulture));
            await AssertBulkExportsExistAsync(
                context,
                legacyExportId,
                identifiedExportId,
                otherActorExportId);
            Assert.Equal(
                3L,
                Convert.ToInt64(
                    await ScalarAsync(
                        context,
                        "SELECT COUNT(*) FROM bulk_transcript_export " +
                        "WHERE request_idempotency_key IS NULL " +
                        "AND request_fingerprint IS NULL;"),
                    CultureInfo.InvariantCulture));
            Assert.Equal(
                2L,
                Convert.ToInt64(
                    await ScalarAsync(
                        context,
                        "SELECT COUNT(*) FROM sqlite_master WHERE type='index' " +
                        "AND name IN (" +
                        "'IX_bulk_transcript_export_created_by_staff_user_id_" +
                        "request_idempotency_key'," +
                        "'IX_bulk_transcript_export_created_by_staff_user_id_state');"),
                    CultureInfo.InvariantCulture));
            Assert.Equal(
                rootsBefore
                    .Where(pair => pair.Key != "bulk_transcript_export")
                    .ToDictionary(pair => pair.Key, pair => pair.Value),
                await LoadRootPagesAsync(
                    context,
                    "submission",
                    "template_version",
                    "background_job",
                    "export_record"));
            await AssertSentinelTriggersExistAsync(context);
            await AssertDatabaseHealthyAsync(context);
            await context.Database.CloseConnectionAsync();

            var appliedMigrations = await context.Database.GetAppliedMigrationsAsync();
            Assert.Contains(Migration0020, appliedMigrations);
            Assert.Contains(Migration0021, appliedMigrations);
            Assert.False(context.Database.HasPendingModelChanges());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static BackgroundJobEntity CreateBackgroundJob(
        string id,
        string deduplicationKey,
        DateTimeOffset now) =>
        new()
        {
            Id = id,
            Type = "bulk_transcript_export.render",
            SchemaVersion = 1,
            DeduplicationKey = deduplicationKey,
            PayloadJson = "{}",
            State = "queued",
            MaxAttempts = 1,
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

    private static Task<int> InsertLegacyBulkExportAsync(
        OokiGraderDbContext context,
        string id,
        string backgroundJobId,
        string staffId,
        long createdAt) =>
        context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO bulk_transcript_export (
                id, background_job_id, selector_json, selector_hash,
                source_snapshot_json, source_fingerprint,
                renderer_version, package_format_version, state,
                student_count, result_count, processed_result_count,
                created_by_staff_user_id, created_at, updated_at, revision)
            VALUES (
                @id, @job, '{{}}',
                'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                '[]',
                'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                'renderer', 'zip-v1', 'queued', 1, 1, 0,
                @staff, @createdAt, @createdAt, 1);
            """,
            new SqliteParameter("@id", id),
            new SqliteParameter("@job", backgroundJobId),
            new SqliteParameter("@staff", staffId),
            new SqliteParameter("@createdAt", createdAt));

    private static Task<int> InsertHardenedBulkExportAsync(
        OokiGraderDbContext context,
        string id,
        string backgroundJobId,
        string staffId,
        string requestIdempotencyKey,
        string requestFingerprint,
        long createdAt) =>
        context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO bulk_transcript_export (
                id, background_job_id,
                request_idempotency_key, request_fingerprint,
                selector_json, selector_hash,
                source_snapshot_json, source_fingerprint,
                renderer_version, package_format_version, state,
                student_count, result_count, processed_result_count,
                created_by_staff_user_id, created_at, updated_at, revision)
            VALUES (
                @id, @job, @requestKey, @requestFingerprint, '{{}}',
                'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                '[]',
                'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                'renderer', 'zip-v1', 'queued', 1, 1, 0,
                @staff, @createdAt, @createdAt, 1);
            """,
            new SqliteParameter("@id", id),
            new SqliteParameter("@job", backgroundJobId),
            new SqliteParameter("@requestKey", requestIdempotencyKey),
            new SqliteParameter("@requestFingerprint", requestFingerprint),
            new SqliteParameter("@staff", staffId),
            new SqliteParameter("@createdAt", createdAt));

    private static async Task AssertSentinelTriggersExistAsync(
        OokiGraderDbContext context)
    {
        Assert.Equal(
            2L,
            Convert.ToInt64(
                await ScalarAsync(
                    context,
                    "SELECT COUNT(*) FROM sqlite_master " +
                    "WHERE type='trigger' AND name IN " +
                    "('trg_0021_submission_sentinel'," +
                    "'trg_0021_export_sentinel');"),
                CultureInfo.InvariantCulture));
    }

    private static async Task AssertDatabaseHealthyAsync(
        OokiGraderDbContext context)
    {
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

    private static async Task AssertBulkExportsExistAsync(
        OokiGraderDbContext context,
        params string[] ids)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        var parameterNames = new string[ids.Length];
        for (var index = 0; index < ids.Length; index++)
        {
            parameterNames[index] = $"@id{index}";
            command.Parameters.Add(
                new SqliteParameter(parameterNames[index], ids[index]));
        }

        command.CommandText =
            "SELECT COUNT(*) FROM bulk_transcript_export WHERE id IN (" +
            string.Join(',', parameterNames) +
            ");";
        Assert.Equal(
            ids.Length,
            Convert.ToInt32(
                await command.ExecuteScalarAsync(),
                CultureInfo.InvariantCulture));
    }

    private static int CountOccurrences(string value, string fragment)
    {
        var count = 0;
        var startIndex = 0;
        while ((startIndex = value.IndexOf(
                   fragment,
                   startIndex,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            startIndex += fragment.Length;
        }

        return count;
    }

    private static async Task<IReadOnlyDictionary<string, long>> LoadRootPagesAsync(
        OokiGraderDbContext context,
        params string[] tables)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            result.Add(
                table,
                Convert.ToInt64(
                    await ScalarAsync(
                        context,
                        $"SELECT rootpage FROM sqlite_master " +
                        $"WHERE type='table' AND name='{table}';"),
                    CultureInfo.InvariantCulture));
        }

        return result;
    }

    private static async Task<object?> ScalarAsync(
        OokiGraderDbContext context,
        string sql)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
