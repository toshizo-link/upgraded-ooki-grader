using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Data.Sqlite;
using System.Globalization;
using OokiGrader.Application.Identifiers;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Infrastructure.Tests;

public sealed class DatabaseFoundationTests
{
    [Fact]
    public async Task InitializerConfiguresWalForeignKeysBusyTimeoutAndBootstrapState()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.Factory.CreateDbContext();
        await context.Database.OpenConnectionAsync();

        var journalMode = await ExecuteScalarAsync(context, "PRAGMA journal_mode;");
        var foreignKeys = await ExecuteScalarAsync(context, "PRAGMA foreign_keys;");
        var busyTimeout = await ExecuteScalarAsync(context, "PRAGMA busy_timeout;");
        var settings = await context.SiteSettings.AsNoTracking().SingleAsync();

        Assert.Equal("wal", journalMode?.ToString(), ignoreCase: true);
        Assert.Equal(1L, Convert.ToInt64(foreignKeys, CultureInfo.InvariantCulture));
        Assert.Equal(5_000L, Convert.ToInt64(busyTimeout, CultureInfo.InvariantCulture));
        Assert.Equal("site", settings.Id);
        Assert.Equal(new string('a', 64), settings.BootstrapTokenHash);
        Assert.NotNull(settings.BootstrapTokenExpiresAt);
    }

    [Fact]
    public async Task DatabaseEnforcesForeignKeysAndCheckConstraints()
    {
        await using var database = await TestDatabase.CreateAsync();

        await using (var context = database.Factory.CreateDbContext())
        {
            context.StudentAliases.Add(new StudentAliasEntity
            {
                Id = UlidId.New(database.Clock.UtcNow),
                StudentId = UlidId.New(database.Clock.UtcNow),
                AliasType = "kana",
                DisplayValue = "オオキ",
                NormalizedValue = "オオキ",
                CreatedByStaffUserId = UlidId.New(database.Clock.UtcNow),
                CreatedAt = database.Clock.UtcNow
            });
            await Assert.ThrowsAsync<DbUpdateException>(
                () => context.SaveChangesAsync());
        }

        await using (var context = database.Factory.CreateDbContext())
        {
            context.BackgroundJobs.Add(new BackgroundJobEntity
            {
                Id = UlidId.New(database.Clock.UtcNow),
                Type = "Invalid",
                SchemaVersion = 1,
                DeduplicationKey = "invalid:progress",
                Priority = 0,
                PayloadJson = "{}",
                State = "queued",
                AttemptCount = 0,
                MaxAttempts = 1,
                NextAttemptAt = database.Clock.UtcNow,
                ProgressBasisPoints = 10_001,
                CreatedAt = database.Clock.UtcNow,
                UpdatedAt = database.Clock.UtcNow
            });
            await Assert.ThrowsAsync<DbUpdateException>(
                () => context.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task RevisionConcurrencyRejectsAStaleWriter()
    {
        await using var database = await TestDatabase.CreateAsync();
        var studentId = UlidId.New(database.Clock.UtcNow);

        await using (var seed = database.Factory.CreateDbContext())
        {
            seed.Students.Add(CreateStudent(studentId, database.Clock.UtcNow));
            await seed.SaveChangesAsync();
        }

        await using var first = database.Factory.CreateDbContext();
        await using var second = database.Factory.CreateDbContext();
        var firstCopy = await first.Students.SingleAsync(student => student.Id == studentId);
        var staleCopy = await second.Students.SingleAsync(student => student.Id == studentId);

        firstCopy.DisplayName = "大木 花子（更新）";
        await first.SaveChangesAsync();
        Assert.Equal(2, firstCopy.Revision);

        staleCopy.DisplayName = "大木 はなこ";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => second.SaveChangesAsync());
    }

    [Fact]
    public async Task AuthenticationPersistenceStoresOnlySessionHashesAndIdempotencyResponses()
    {
        await using var database = await TestDatabase.CreateAsync();
        var staffId = UlidId.New(database.Clock.UtcNow);

        await using (var context = database.Factory.CreateDbContext())
        {
            context.StaffUsers.Add(new StaffUserEntity
            {
                Id = staffId,
                Username = "Teacher.One",
                UsernameNormalized = "teacher.one",
                DisplayName = "先生",
                PasswordHash = "argon2id:bounded-test-value",
                PasswordAlgorithm = "argon2id",
                PasswordAlgorithmVersion = 1,
                Status = "active",
                CredentialChangedAt = database.Clock.UtcNow,
                CreatedAt = database.Clock.UtcNow,
                UpdatedAt = database.Clock.UtcNow
            });
            context.StaffSessions.Add(new StaffSessionEntity
            {
                IdHash = new string('b', 64),
                StaffUserId = staffId,
                CreatedAt = database.Clock.UtcNow,
                LastSeenAt = database.Clock.UtcNow,
                IdleExpiresAt = database.Clock.UtcNow.AddHours(1),
                AbsoluteExpiresAt = database.Clock.UtcNow.AddHours(8),
                CsrfSecretHash = new string('c', 64)
            });
            context.IdempotencyRecords.Add(new IdempotencyRecordEntity
            {
                Id = UlidId.New(database.Clock.UtcNow),
                ActorKey = staffId,
                Route = "POST:/api/v1/uploads",
                IdempotencyKey = Guid.NewGuid().ToString(),
                CanonicalRequestHash = new string('d', 64),
                ResponseStatusCode = 201,
                ResponseContentType = "application/json",
                ResponseBodyJson = """{"uploadId":"test"}""",
                CreatedAt = database.Clock.UtcNow,
                ExpiresAt = database.Clock.UtcNow.AddHours(24)
            });
            await context.SaveChangesAsync();
        }

        await using var verify = database.Factory.CreateDbContext();
        var session = await verify.StaffSessions.AsNoTracking().SingleAsync();
        var idempotency = await verify.IdempotencyRecords.AsNoTracking().SingleAsync();
        var sessionProperties = verify.Model
            .FindEntityType(typeof(StaffSessionEntity))!
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal(new string('b', 64), session.IdHash);
        Assert.DoesNotContain(
            sessionProperties,
            property => property.Equals("Token", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(201, idempotency.ResponseStatusCode);
    }

    [Fact]
    public async Task LatestMigrationPreservesDataAndAddsNonModelSourceRole()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "ooki-grader-migration-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);

        try
        {
            var databasePath = Path.Combine(rootPath, "upgrade.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                ForeignKeys = true,
                DefaultTimeout = 5,
                Pooling = false
            }.ToString();
            var options = new DbContextOptionsBuilder<OokiGraderDbContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(new SqlitePragmaConnectionInterceptor())
                .Options;
            var clock = new TestClock(new DateTimeOffset(
                2026,
                7,
                27,
                3,
                0,
                0,
                TimeSpan.Zero));

            await using var context = new OokiGraderDbContext(options, clock);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20260727074210_0009_GeminiAiCore");

            const string questionId = "01K13MIGRATIONQUESTION001";

            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO test_template (
                    id, title, state, created_by_staff_user_id,
                    created_at, updated_at, revision, default_points_milli)
                VALUES (
                    '01K13MIGRATIONTEMPLATE001', '移行テスト', 'draft',
                    '01K13MIGRATIONSTAFF00001', 1, 1, 1, 1000);
                """);
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO template_version (
                    id, test_template_id, version_number, state,
                    default_allow_non_kanji, pipeline_version,
                    created_at, updated_at, revision, default_points_milli)
                VALUES (
                    '01K13MIGRATIONVERSION0001', '01K13MIGRATIONTEMPLATE001',
                    1, 'draft', 0, 'test', 1, 1, 1, 1000);
                """);
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO upload_session (
                    id, created_by_staff_user_id, purpose, destination_type,
                    original_file_name, declared_mime_type, expected_bytes,
                    current_bytes, incoming_relative_path, state, expires_at,
                    created_at, updated_at, revision)
                VALUES (
                    '01K13MIGRATIONUPLOAD00001',
                    '01K13MIGRATIONSTAFF00001', 'template_source',
                    'template_source', '問題用紙.pdf', 'application/pdf',
                    0, 0, 'migration/source', 'completed', 2, 1, 1, 1);
                """);
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO template_source (
                    id, template_version_id, upload_session_id,
                    file_reference_id, source_role, display_name, ordinal,
                    uploaded_by_staff_user_id, created_at)
                VALUES (
                    '01K13MIGRATIONSOURCE00001',
                    '01K13MIGRATIONVERSION0001',
                    '01K13MIGRATIONUPLOAD00001', NULL, 'blank_test',
                    '問題用紙.pdf', 0, '01K13MIGRATIONSTAFF00001', 1);
                """);
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO question (
                    id, template_version_id, logical_question_id, order_index,
                    display_label, question_text, question_type, grading_mode,
                    max_points_milli, allow_non_kanji, requires_review_always,
                    teacher_verified, created_at, updated_at, revision)
                VALUES (
                    '01K13MIGRATIONQUESTION001', '01K13MIGRATIONVERSION0001',
                    '01K13MIGRATIONLOGICALQ001', 0, '1', '問題',
                    'subjective', 'manual', 1000, 0, 1, 1, 1, 1, 1);
                """);

            // Exercise the historical data-preservation migrations first,
            // then model the production 0016 state where the initializer has
            // already installed all integrity triggers before 0017 runs.
            await migrator.MigrateAsync(
                "20260806000000_0016_OpenRouterProviders");

            foreach (var triggerStatement in
                     TemplateVersionIntegrityTriggerCatalog.Schema16Statements)
            {
                await context.Database.ExecuteSqlRawAsync(triggerStatement);
            }

            await migrator.MigrateAsync();
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO template_source (
                    id, template_version_id, upload_session_id,
                    file_reference_id, source_role, display_name, ordinal,
                    uploaded_by_staff_user_id, created_at)
                VALUES (
                    '01K13MIGRATIONSOURCE00002',
                    '01K13MIGRATIONVERSION0001',
                    '01K13MIGRATIONUPLOAD00001', NULL,
                    'contains_non_model_answers', '生徒答案.pdf', 1,
                    '01K13MIGRATIONSTAFF00001', 2);
                """);
            await context.Database.OpenConnectionAsync();

            var increment = await ExecuteScalarAsync(
                context,
                $"SELECT point_increment_milli FROM question WHERE id = '{questionId}';");
            var requiresCompleteAnswer = await ExecuteScalarAsync(
                context,
                $"SELECT requires_complete_answer FROM question WHERE id = '{questionId}';");
            var answerOrderInsensitive = await ExecuteScalarAsync(
                context,
                $"SELECT answer_order_insensitive FROM question WHERE id = '{questionId}';");
            var originalSourceRole = await ExecuteScalarAsync(
                context,
                "SELECT source_role FROM template_source WHERE ordinal = 0;");
            var nonModelSourceCount = await ExecuteScalarAsync(
                context,
                "SELECT COUNT(*) FROM template_source " +
                "WHERE source_role = 'contains_non_model_answers';");
            var historicalTestType = await ExecuteScalarAsync(
                context,
                "SELECT test_type FROM template_version " +
                "WHERE id = '01K13MIGRATIONVERSION0001';");
            var generationBatchTableCount = await ExecuteScalarAsync(
                context,
                "SELECT COUNT(*) FROM sqlite_master " +
                "WHERE type = 'table' AND name = 'template_generation_batch';");
            var generationUnitTableCount = await ExecuteScalarAsync(
                context,
                "SELECT COUNT(*) FROM sqlite_master " +
                "WHERE type = 'table' AND name = 'template_generation_unit';");
            var preservedTriggerCount = await ExecuteScalarAsync(
                context,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' " +
                "AND name IN (" +
                "'trg_test_session_requires_published_template_insert'," +
                "'trg_published_template_version_content_immutable'," +
                "'trg_published_answer_no_delete');");
            var publishedVersionTriggerSql = await ExecuteScalarAsync(
                context,
                "SELECT sql FROM sqlite_master WHERE type = 'trigger' " +
                "AND name = 'trg_published_template_version_content_immutable';");
            var integrity = await ExecuteScalarAsync(context, "PRAGMA integrity_check;");
            var foreignKeyViolation = await ExecuteScalarAsync(
                context,
                "SELECT COUNT(*) FROM pragma_foreign_key_check;");

            Assert.Equal(1L, Convert.ToInt64(increment, CultureInfo.InvariantCulture));
            Assert.Equal(
                0L,
                Convert.ToInt64(
                    requiresCompleteAnswer,
                    CultureInfo.InvariantCulture));
            Assert.Equal(
                0L,
                Convert.ToInt64(
                    answerOrderInsensitive,
                    CultureInfo.InvariantCulture));
            Assert.Equal("blank_test", originalSourceRole?.ToString());
            Assert.Equal(
                1L,
                Convert.ToInt64(nonModelSourceCount, CultureInfo.InvariantCulture));
            Assert.Equal(DBNull.Value, historicalTestType);
            Assert.Equal(
                1L,
                Convert.ToInt64(generationBatchTableCount, CultureInfo.InvariantCulture));
            Assert.Equal(
                1L,
                Convert.ToInt64(generationUnitTableCount, CultureInfo.InvariantCulture));
            Assert.Equal(
                3L,
                Convert.ToInt64(preservedTriggerCount, CultureInfo.InvariantCulture));
            Assert.Contains(
                "generation_profile_hash",
                publishedVersionTriggerSql?.ToString(),
                StringComparison.Ordinal);
            Assert.Equal("ok", integrity?.ToString(), ignoreCase: true);
            Assert.Equal(
                0L,
                Convert.ToInt64(foreignKeyViolation, CultureInfo.InvariantCulture));

            await context.Database.CloseConnectionAsync();
            await migrator.MigrateAsync(
                "20260806000000_0016_OpenRouterProviders");
            await context.Database.OpenConnectionAsync();

            var downgradedGenerationColumnCount = await ExecuteScalarAsync(
                context,
                "SELECT COUNT(*) FROM pragma_table_info('template_version') " +
                "WHERE name IN ('test_type','generation_profile_hash');");
            var downgradedGenerationTableCount = await ExecuteScalarAsync(
                context,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' " +
                "AND name = 'template_generation_batch';");
            var downgradedTriggerCount = await ExecuteScalarAsync(
                context,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' " +
                "AND name IN (" +
                "'trg_test_session_requires_published_template_insert'," +
                "'trg_published_template_version_content_immutable'," +
                "'trg_published_answer_no_delete');");
            var downgradedVersionTriggerSql = await ExecuteScalarAsync(
                context,
                "SELECT sql FROM sqlite_master WHERE type = 'trigger' " +
                "AND name = 'trg_published_template_version_content_immutable';");

            Assert.Equal(
                0L,
                Convert.ToInt64(
                    downgradedGenerationColumnCount,
                    CultureInfo.InvariantCulture));
            Assert.Equal(
                0L,
                Convert.ToInt64(
                    downgradedGenerationTableCount,
                    CultureInfo.InvariantCulture));
            Assert.Equal(
                3L,
                Convert.ToInt64(downgradedTriggerCount, CultureInfo.InvariantCulture));
            Assert.DoesNotContain(
                "generation_profile_hash",
                downgradedVersionTriggerSql?.ToString(),
                StringComparison.Ordinal);

            await context.Database.CloseConnectionAsync();
            await migrator.MigrateAsync();
            await context.Database.OpenConnectionAsync();

            var reappliedGenerationColumnCount = await ExecuteScalarAsync(
                context,
                "SELECT COUNT(*) FROM pragma_table_info('template_version') " +
                "WHERE name IN ('test_type','generation_profile_hash');");
            var reappliedIntegrity = await ExecuteScalarAsync(
                context,
                "PRAGMA integrity_check;");
            var reappliedForeignKeyViolation = await ExecuteScalarAsync(
                context,
                "SELECT COUNT(*) FROM pragma_foreign_key_check;");

            Assert.Equal(
                2L,
                Convert.ToInt64(
                    reappliedGenerationColumnCount,
                    CultureInfo.InvariantCulture));
            Assert.Equal("ok", reappliedIntegrity?.ToString(), ignoreCase: true);
            Assert.Equal(
                0L,
                Convert.ToInt64(
                    reappliedForeignKeyViolation,
                    CultureInfo.InvariantCulture));
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private static StudentEntity CreateStudent(string id, DateTimeOffset now)
    {
        return new StudentEntity
        {
            Id = id,
            StudentNumber = "S-1042",
            StudentNumberNormalized = "S-1042",
            FamilyName = "大木",
            GivenName = "花子",
            FamilyNameNormalized = "大木",
            GivenNameNormalized = "花子",
            FamilyNameKana = "オオキ",
            GivenNameKana = "ハナコ",
            FamilyNameKanaNormalized = "オオキ",
            GivenNameKanaNormalized = "ハナコ",
            DisplayName = "大木 花子",
            Status = "active",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static async Task<object?> ExecuteScalarAsync(
        Microsoft.EntityFrameworkCore.DbContext context,
        string commandText)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = commandText;
        return await command.ExecuteScalarAsync();
    }
}
