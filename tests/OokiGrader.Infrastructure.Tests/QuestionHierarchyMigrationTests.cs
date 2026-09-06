using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OokiGrader.Infrastructure.Persistence;

namespace OokiGrader.Infrastructure.Tests;

public sealed class QuestionHierarchyMigrationTests
{
    private const string Migration0023 =
        "20260827220405_0023_ConfigurableGeminiModel";
    private const string Migration0024 =
        "20260827223000_0024_QuestionHierarchy";

    [Theory]
    [InlineData("draft")]
    [InlineData("published")]
    [InlineData("superseded")]
    [InlineData("retired")]
    public async Task Migration0024PreservesHistoryAndRestoresPublishedQuestionProtection(
        string versionState)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ooki-question-hierarchy-migration-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new DbContextOptionsBuilder<OokiGraderDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(root, "hierarchy.db"),
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    ForeignKeys = true,
                    DefaultTimeout = 5,
                    Pooling = false,
                }.ToString())
                .AddInterceptors(new SqlitePragmaConnectionInterceptor())
                .Options;
            await using var context = new OokiGraderDbContext(options);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(Migration0023);

            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO test_template (
                    id, title, state, created_by_staff_user_id,
                    created_at, updated_at, revision, default_points_milli)
                VALUES (
                    '01KZHIERARCHYTEMPLATE0001', '階層移行テスト', 'draft',
                    '01KZHIERARCHYSTAFF000001', 1, 1, 1, 1000);

                INSERT INTO template_version (
                    id, test_template_id, version_number, state,
                    default_allow_non_kanji, pipeline_version,
                    created_at, updated_at, revision, default_points_milli)
                VALUES (
                    '01KZHIERARCHYVERSION00001',
                    '01KZHIERARCHYTEMPLATE0001', 1, 'draft', 0, 'test',
                    1, 1, 1, 1000);

                INSERT INTO question (
                    id, template_version_id, logical_question_id, order_index,
                    display_label, question_text, question_type, grading_mode,
                    max_points_milli, allow_non_kanji, requires_review_always,
                    teacher_verified, teacher_note, created_at, updated_at,
                    revision)
                VALUES (
                    '01KZHIERARCHYQUESTION0001',
                    '01KZHIERARCHYVERSION00001',
                    '01KZHIERARCHYLOGICAL00001', 0, '問1', '説明しなさい。',
                    'subjective', 'manual', 1000, 0, 0, 0,
                    '[AI確認] [question.filled_answer_redacted] 解答を除去しました。' ||
                    char(10) || '先生の追記' || char(10) ||
                    '[AI確認] [answer.expected_answer_missing] 正答が未解決です。' ||
                    char(10) || '次回も確認',
                    1, 1, 1);
                """);

            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE template_version SET state={versionState}, published_at=1, published_by_staff_user_id='01KZHIERARCHYSTAFF000001', content_hash={'a' + new string('b', 63)};");
            foreach (var statement in TemplateVersionIntegrityTriggerCatalog.Schema18Statements)
            {
                await context.Database.ExecuteSqlRawAsync(statement);
            }
            var originalNote = await ScalarStringAsync(
                context, "SELECT teacher_note FROM question;");
            var originalGuard = await ScalarStringAsync(
                context,
                "SELECT sql FROM sqlite_master WHERE name='trg_published_question_no_update';");

            await migrator.MigrateAsync(Migration0024);

            Assert.Equal(
                versionState == "draft" ? "先生の追記\n次回も確認" : originalNote,
                await ScalarStringAsync(
                    context,
                    "SELECT teacher_note FROM question WHERE " +
                    "id='01KZHIERARCHYQUESTION0001';"));
            var machineReview = await ScalarStringAsync(
                context,
                "SELECT extraction_review_json FROM question WHERE " +
                "id='01KZHIERARCHYQUESTION0001';");
            if (versionState == "draft")
            {
                Assert.Contains(
                    "question.filled_answer_redacted",
                    machineReview,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "answer.expected_answer_missing",
                    machineReview,
                    StringComparison.Ordinal);
            }
            else
            {
                Assert.Empty(machineReview);
            }
            Assert.DoesNotContain(
                "先生の追記",
                machineReview,
                StringComparison.Ordinal);
            Assert.Equal(
                "0",
                await ScalarStringAsync(
                    context,
                    "SELECT teacher_verified FROM question WHERE " +
                    "id='01KZHIERARCHYQUESTION0001';"));
            Assert.Equal(
                "問1",
                await ScalarStringAsync(
                    context,
                    "SELECT middle_question_label FROM question WHERE " +
                    "id='01KZHIERARCHYQUESTION0001';"));

            Assert.Equal(originalGuard, await ScalarStringAsync(context,
                "SELECT sql FROM sqlite_master WHERE name='trg_published_question_no_update';"));
            Assert.Equal('a' + new string('b', 63), await ScalarStringAsync(context,
                "SELECT content_hash FROM template_version;"));
            if (versionState != "draft")
            {
                var immutable = await Assert.ThrowsAsync<SqliteException>(() =>
                    context.Database.ExecuteSqlRawAsync("UPDATE question SET question_text='changed';"));
                Assert.Contains("published_template_content_is_immutable", immutable.Message,
                    StringComparison.Ordinal);
                await migrator.MigrateAsync(Migration0023);
                Assert.Equal(originalNote, await ScalarStringAsync(context,
                    "SELECT teacher_note FROM question;"));
                Assert.Equal(originalGuard, await ScalarStringAsync(context,
                    "SELECT sql FROM sqlite_master WHERE name='trg_published_question_no_update';"));
                await migrator.MigrateAsync(Migration0024);
                Assert.Equal(originalNote, await ScalarStringAsync(context,
                    "SELECT teacher_note FROM question;"));
                return;
            }

            var mixedScoringInsert = await Assert.ThrowsAsync<SqliteException>(
                () => context.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO question (
                        id, template_version_id, logical_question_id,
                        order_index, display_label, major_question_label,
                        middle_question_label, minor_question_label,
                        hierarchy_path_key, question_text, question_type,
                        grading_mode, max_points_milli, allow_non_kanji,
                        requires_review_always, teacher_verified, created_at,
                        updated_at, revision)
                    VALUES (
                        '01KZHIERARCHYQUESTION0002',
                        '01KZHIERARCHYVERSION00001',
                        '01KZHIERARCHYLOGICAL00002', 1, '問1 (1)', NULL,
                        '問1', '(1)', char(31) || '問1' || char(31) || '(1)',
                        '答えなさい。', 'exact_short_text',
                        'transcribe_then_rules', 1000, 0, 0, 1, 2, 2, 1);
                    """));
            Assert.Equal(19, mixedScoringInsert.SqliteErrorCode);
            Assert.Contains(
                "hierarchy scoring mode conflict",
                mixedScoringInsert.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> ScalarStringAsync(
        OokiGraderDbContext context,
        string sql)
    {
        await context.Database.OpenConnectionAsync();
        try
        {
            await using var command = context.Database
                .GetDbConnection()
                .CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(
                    await command.ExecuteScalarAsync(),
                    System.Globalization.CultureInfo.InvariantCulture)
                ?? string.Empty;
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }
}
