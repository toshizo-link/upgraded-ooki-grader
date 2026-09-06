using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OokiGrader.Infrastructure.Persistence;

#nullable disable

namespace OokiGrader.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OokiGraderDbContext))]
[Migration("20260827223000_0024_QuestionHierarchy")]
public partial class _0024_QuestionHierarchy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "major_question_label",
            table: "question",
            type: "TEXT",
            maxLength: 100,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "middle_question_label",
            table: "question",
            type: "TEXT",
            maxLength: 100,
            nullable: false,
            defaultValue: "");
        migrationBuilder.AddColumn<string>(
            name: "minor_question_label",
            table: "question",
            type: "TEXT",
            maxLength: 100,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "hierarchy_path_key",
            table: "question",
            type: "TEXT",
            maxLength: 350,
            nullable: false,
            defaultValue: "");
        migrationBuilder.AddColumn<string>(
            name: "extraction_review_json",
            table: "question",
            type: "TEXT",
            maxLength: 20000,
            nullable: true);

        // Existing installations already enforce immutable published questions.
        // Suspend only that update guard, within this migration's transaction,
        // while deriving the new columns from the unchanged historical label.
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_published_question_no_update;");
        migrationBuilder.Sql(
            "UPDATE question SET middle_question_label = display_label, " +
            "hierarchy_path_key = char(31) || display_label || char(31);");
        migrationBuilder.Sql(
            TemplateVersionIntegrityTriggerCatalog.Schema18PublishedQuestionUpdateStatement);

        // Separate legacy machine markers line-by-line. A legacy Notes value can
        // contain both [AI確認] lines and teacher-authored lines; only the former
        // belong in machine-owned review metadata.
        migrationBuilder.Sql(
            """
            WITH RECURSIVE note_lines(question_id, line_number, line, rest) AS (
                SELECT id, 0, '',
                    replace(replace(teacher_note, char(13) || char(10), char(10)),
                        char(13), char(10)) || char(10)
                FROM question
                WHERE teacher_note IS NOT NULL
                    AND template_version_id IN (
                        SELECT id FROM template_version WHERE state = 'draft')
                UNION ALL
                SELECT question_id, line_number + 1,
                    substr(rest, 1, instr(rest, char(10)) - 1),
                    substr(rest, instr(rest, char(10)) + 1)
                FROM note_lines
                WHERE rest <> ''
            )
            UPDATE question
            SET extraction_review_json = (
                    SELECT json_group_array(trim(line))
                    FROM note_lines
                    WHERE question_id = question.id
                        AND ltrim(line) LIKE '[AI確認]%'
                ),
                teacher_note = (
                    SELECT group_concat(line, char(10))
                    FROM note_lines
                    WHERE question_id = question.id
                        AND ltrim(line) NOT LIKE '[AI確認]%'
                        AND trim(line) <> ''
                )
            WHERE id IN (
                SELECT question_id
                FROM note_lines
                WHERE ltrim(line) LIKE '[AI確認]%'
            );
            """);
        migrationBuilder.Sql(
            "UPDATE question SET rubric_text = NULL WHERE rubric_text = " +
            "'模範解答と照合し、内容と根拠が一致する場合のみ正解とします。' || " +
            "'部分的な一致、曖昧な表現、別解の可能性がある場合は点数を確定せず、' || " +
            "'先生の確認に回します。' AND template_version_id IN " +
            "(SELECT id FROM template_version WHERE state = 'draft');");
        migrationBuilder.Sql(
            "UPDATE question SET kanji_policy_note = NULL WHERE kanji_policy_note = " +
            "'AIによる表記方針の提案です。先生の確認が必要です。' " +
            "AND template_version_id IN " +
            "(SELECT id FROM template_version WHERE state = 'draft');");

        migrationBuilder.DropIndex(
            name: "IX_question_template_version_id_display_label",
            table: "question");
        migrationBuilder.CreateIndex(
            name: "IX_question_template_version_id_hierarchy_path_key",
            table: "question",
            columns: new[] { "template_version_id", "hierarchy_path_key" },
            unique: true);
        migrationBuilder.Sql(
            """
            CREATE TRIGGER trg_question_hierarchy_scoring_mode_insert
            BEFORE INSERT ON question
            WHEN EXISTS (
                SELECT 1
                FROM question AS existing
                WHERE existing.template_version_id = NEW.template_version_id
                    AND ifnull(existing.major_question_label, '') =
                        ifnull(NEW.major_question_label, '')
                    AND existing.middle_question_label = NEW.middle_question_label
                    AND (existing.minor_question_label IS NULL) <>
                        (NEW.minor_question_label IS NULL)
            )
            BEGIN
                SELECT RAISE(ABORT, 'question hierarchy scoring mode conflict');
            END;
            """);
        migrationBuilder.Sql(
            """
            CREATE TRIGGER trg_question_hierarchy_scoring_mode_update
            BEFORE UPDATE OF template_version_id, major_question_label,
                middle_question_label, minor_question_label ON question
            WHEN EXISTS (
                SELECT 1
                FROM question AS existing
                WHERE existing.id <> NEW.id
                    AND existing.template_version_id = NEW.template_version_id
                    AND ifnull(existing.major_question_label, '') =
                        ifnull(NEW.major_question_label, '')
                    AND existing.middle_question_label = NEW.middle_question_label
                    AND (existing.minor_question_label IS NULL) <>
                        (NEW.minor_question_label IS NULL)
            )
            BEGIN
                SELECT RAISE(ABORT, 'question hierarchy scoring mode conflict');
            END;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "DROP TRIGGER IF EXISTS trg_question_hierarchy_scoring_mode_insert;");
        migrationBuilder.Sql(
            "DROP TRIGGER IF EXISTS trg_question_hierarchy_scoring_mode_update;");
        migrationBuilder.Sql(
            "UPDATE question SET teacher_note = CASE " +
            "WHEN teacher_note IS NULL THEN " +
            "(SELECT group_concat(value, char(10)) " +
            "FROM json_each(question.extraction_review_json)) " +
            "ELSE (SELECT group_concat(value, char(10)) " +
            "FROM json_each(question.extraction_review_json)) || char(10) || teacher_note " +
            "END WHERE extraction_review_json IS NOT NULL " +
            "AND template_version_id IN " +
            "(SELECT id FROM template_version WHERE state = 'draft');");
        migrationBuilder.DropIndex(
            name: "IX_question_template_version_id_hierarchy_path_key",
            table: "question");
        migrationBuilder.CreateIndex(
            name: "IX_question_template_version_id_display_label",
            table: "question",
            columns: new[] { "template_version_id", "display_label" },
            unique: true);
        // These fields are additive and no remaining index/constraint refers to
        // them. Native SQLite DROP COLUMN avoids EF's unsupported/rebuild path
        // and preserves independently managed objects during migration tests.
        migrationBuilder.Sql(
            "ALTER TABLE \"question\" DROP COLUMN \"extraction_review_json\";");
        migrationBuilder.Sql(
            "ALTER TABLE \"question\" DROP COLUMN \"hierarchy_path_key\";");
        migrationBuilder.Sql(
            "ALTER TABLE \"question\" DROP COLUMN \"major_question_label\";");
        migrationBuilder.Sql(
            "ALTER TABLE \"question\" DROP COLUMN \"middle_question_label\";");
        migrationBuilder.Sql(
            "ALTER TABLE \"question\" DROP COLUMN \"minor_question_label\";");
    }
}
