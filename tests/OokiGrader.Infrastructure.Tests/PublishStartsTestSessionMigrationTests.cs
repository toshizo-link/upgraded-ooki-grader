using System.Globalization;
using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OokiGrader.Infrastructure.Persistence;

namespace OokiGrader.Infrastructure.Tests;

public sealed class PublishStartsTestSessionMigrationTests
{
    private const string Migration0021 =
        "20260810190000_0021_BulkTranscriptExportHardening";
    private const string Migration0022 =
        "20260811000000_0022_PublishStartsTestSession";

    [Fact]
    public async Task Migration0022IsAdditiveTriggerSafeAndRoundTripsFrom0021()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ooki-publish-session-migration-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new DbContextOptionsBuilder<OokiGraderDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(root, "publish-session.db"),
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    ForeignKeys = true,
                    DefaultTimeout = 5,
                    Pooling = false,
                }.ToString())
                .AddInterceptors(new SqlitePragmaConnectionInterceptor())
                .Options;
            await using var context = new OokiGraderDbContext(options);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(Migration0021);

            await context.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER trg_0022_test_session_sentinel
                AFTER UPDATE ON test_session BEGIN SELECT 1; END;

                INSERT INTO test_template (
                    id, title, subject, category, course, grade_label,
                    state, created_by_staff_user_id, created_at, updated_at,
                    revision, default_points_milli)
                VALUES (
                    '01KZPUBLISHSESSIONTEMPLATE', '公開時タイトル', '理科',
                    'STEP', '標準', '小学6年', 'active',
                    '01KZPUBLISHSESSIONSTAFF01', 1, 1, 1, 1000);

                INSERT INTO template_version (
                    id, test_template_id, version_number, state,
                    default_allow_non_kanji, pipeline_version,
                    published_by_staff_user_id, published_at, content_hash,
                    created_at, updated_at, revision, default_points_milli)
                VALUES (
                    '01KZPUBLISHSESSIONVERSION1',
                    '01KZPUBLISHSESSIONTEMPLATE', 1, 'published', 0, 'test',
                    '01KZPUBLISHSESSIONSTAFF01', 1,
                    'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                    1, 1, 1, 1000);

                INSERT INTO test_session (
                    id, template_version_id, title_override, test_date, course,
                    class_label, priority, state, expected_roster_enabled,
                    created_by_staff_user_id, created_at, updated_at, revision)
                VALUES (
                    '01KZPUBLISHSESSIONLEGACY01',
                    '01KZPUBLISHSESSIONVERSION1', NULL, '2026-08-10',
                    '実施コース', 'A組', 'economy', 'closed', 0,
                    '01KZPUBLISHSESSIONSTAFF01', 1, 1, 1);
                """);

            await context.Database.OpenConnectionAsync();
            var rootPage = await LongScalarAsync(
                context,
                "SELECT rootpage FROM sqlite_master " +
                "WHERE type='table' AND name='test_session';");
            await context.Database.CloseConnectionAsync();

            var upScript = migrator.GenerateScript(Migration0021, Migration0022);
            Assert.Equal(
                8,
                CountOccurrences(upScript, "ALTER TABLE \"test_session\" ADD "));
            Assert.Contains(
                "CREATE UNIQUE INDEX \"ux_test_session_template_publish\"",
                upScript,
                StringComparison.Ordinal);
            Assert.Contains(
                "CREATE UNIQUE INDEX \"IX_test_session_created_by_staff_user_id_" +
                "request_idempotency_key\"",
                upScript,
                StringComparison.Ordinal);
            Assert.DoesNotContain("ef_temp_", upScript, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DROP TABLE", upScript, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("RENAME TO", upScript, StringComparison.OrdinalIgnoreCase);

            var downScript = migrator.GenerateScript(Migration0022, Migration0021);
            Assert.Equal(
                8,
                CountOccurrences(
                    downScript,
                    "ALTER TABLE \"test_session\" DROP COLUMN"));
            Assert.DoesNotContain("ef_temp_", downScript, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DROP TABLE", downScript, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("RENAME TO", downScript, StringComparison.OrdinalIgnoreCase);

            await migrator.MigrateAsync(Migration0022);
            await AssertHealthyAndUnrebuiltAsync(context, rootPage);
            Assert.Equal(
                2L,
                await LongScalarAsync(
                    context,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type='index' " +
                    "AND name IN ('ux_test_session_template_publish'," +
                    "'IX_test_session_created_by_staff_user_id_" +
                    "request_idempotency_key');"));
            Assert.Equal(
                "公開時タイトル",
                (await ScalarAsync(
                    context,
                    "SELECT template_title_snapshot FROM test_session " +
                    "WHERE id='01KZPUBLISHSESSIONLEGACY01';"))?.ToString());
            Assert.Equal(
                "理科",
                (await ScalarAsync(
                    context,
                    "SELECT template_subject_snapshot FROM test_session " +
                    "WHERE id='01KZPUBLISHSESSIONLEGACY01';"))?.ToString());
            Assert.Equal(
                "小学6年",
                (await ScalarAsync(
                    context,
                    "SELECT template_grade_label_snapshot FROM test_session " +
                    "WHERE id='01KZPUBLISHSESSIONLEGACY01';"))?.ToString());
            Assert.Equal(
                "STEP",
                (await ScalarAsync(
                    context,
                    "SELECT template_category_snapshot FROM test_session " +
                    "WHERE id='01KZPUBLISHSESSIONLEGACY01';"))?.ToString());
            Assert.Equal(
                "標準",
                (await ScalarAsync(
                    context,
                    "SELECT template_course_snapshot FROM test_session " +
                    "WHERE id='01KZPUBLISHSESSIONLEGACY01';"))?.ToString());

            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO test_session (
                    id, template_version_id, creation_source,
                    template_title_snapshot, test_date, priority, state,
                    expected_roster_enabled, created_by_staff_user_id,
                    created_at, updated_at, revision)
                VALUES (
                    '01KZPUBLISHSESSIONATOMIC1',
                    '01KZPUBLISHSESSIONVERSION1', 'template_publish',
                    '公開時タイトル', '2026-08-11', 'expedite', 'open', 0,
                    '01KZPUBLISHSESSIONSTAFF01', 2, 2, 1);
                """);
            var duplicate = await Assert.ThrowsAsync<SqliteException>(() =>
                context.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO test_session (
                        id, template_version_id, creation_source,
                        template_title_snapshot, test_date, priority, state,
                        expected_roster_enabled, created_by_staff_user_id,
                        created_at, updated_at, revision)
                    VALUES (
                        '01KZPUBLISHSESSIONATOMIC2',
                        '01KZPUBLISHSESSIONVERSION1', 'template_publish',
                        '公開時タイトル', '2026-08-12', 'expedite', 'open', 0,
                        '01KZPUBLISHSESSIONSTAFF01', 3, 3, 1);
                    """));
            Assert.Equal(19, duplicate.SqliteErrorCode);

            await migrator.MigrateAsync(Migration0021);
            await AssertHealthyAndUnrebuiltAsync(context, rootPage);
            Assert.Equal(
                2L,
                await LongScalarAsync(context, "SELECT COUNT(*) FROM test_session;"));
            Assert.Equal(
                0L,
                await LongScalarAsync(
                    context,
                    "SELECT COUNT(*) FROM pragma_table_info('test_session') " +
                    "WHERE name IN ('creation_source','template_title_snapshot'," +
                    "'template_subject_snapshot','template_grade_label_snapshot'," +
                    "'template_category_snapshot','template_course_snapshot'," +
                    "'request_idempotency_key','request_fingerprint');"));

            await migrator.MigrateAsync(Migration0022);
            await AssertHealthyAndUnrebuiltAsync(context, rootPage);
            Assert.Equal(
                2L,
                await LongScalarAsync(context, "SELECT COUNT(*) FROM test_session;"));
            Assert.Equal(
                2L,
                await LongScalarAsync(
                    context,
                    "SELECT COUNT(*) FROM test_session " +
                    "WHERE creation_source='manual' " +
                    "AND template_title_snapshot='公開時タイトル';"));
            Assert.Contains(
                Migration0022,
                await context.Database.GetAppliedMigrationsAsync());
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
            Assert.False(context.Database.HasPendingModelChanges());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertHealthyAndUnrebuiltAsync(
        OokiGraderDbContext context,
        long expectedRootPage)
    {
        await context.Database.OpenConnectionAsync();
        Assert.Equal(
            expectedRootPage,
            await LongScalarAsync(
                context,
                "SELECT rootpage FROM sqlite_master " +
                "WHERE type='table' AND name='test_session';"));
        Assert.Equal(
            1L,
            await LongScalarAsync(
                context,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' " +
                "AND name='trg_0022_test_session_sentinel';"));
        Assert.Equal(
            0L,
            await LongScalarAsync(
                context,
                "SELECT COUNT(*) FROM sqlite_master " +
                "WHERE name LIKE 'ef_temp_%';"));
        Assert.Equal(
            "ok",
            (await ScalarAsync(context, "PRAGMA integrity_check;"))?.ToString(),
            ignoreCase: true);
        Assert.Equal(
            0L,
            await LongScalarAsync(
                context,
                "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
        await context.Database.CloseConnectionAsync();
    }

    private static int CountOccurrences(string value, string fragment)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(
                   fragment,
                   index,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += fragment.Length;
        }

        return count;
    }

    private static async Task<long> LongScalarAsync(
        OokiGraderDbContext context,
        string sql) =>
        Convert.ToInt64(
            await ScalarAsync(context, sql),
            CultureInfo.InvariantCulture);

    private static async Task<object?> ScalarAsync(
        OokiGraderDbContext context,
        string sql)
    {
        var connection = context.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;
        if (close)
        {
            await context.Database.OpenConnectionAsync();
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteScalarAsync();
        }
        finally
        {
            if (close)
            {
                await context.Database.CloseConnectionAsync();
            }
        }
    }
}
