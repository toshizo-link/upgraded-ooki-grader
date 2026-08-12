using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OokiGrader.Infrastructure.Persistence.Migrations;

public partial class _0022_PublishStartsTestSession : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "creation_source",
            table: "test_session",
            type: "TEXT",
            maxLength: 32,
            nullable: false,
            defaultValue: "manual");

        migrationBuilder.AddColumn<string>(
            name: "template_title_snapshot",
            table: "test_session",
            type: "TEXT",
            maxLength: 500,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "template_subject_snapshot",
            table: "test_session",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "template_grade_label_snapshot",
            table: "test_session",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "template_category_snapshot",
            table: "test_session",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "template_course_snapshot",
            table: "test_session",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "request_idempotency_key",
            table: "test_session",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "request_fingerprint",
            table: "test_session",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        // Preserve the metadata that legacy sessions currently resolve through
        // their pinned immutable template version. New writes fill snapshots
        // directly and never rely on mutable template display metadata.
        migrationBuilder.Sql(
            """
            UPDATE test_session
            SET template_title_snapshot = (
                    SELECT test_template.title
                    FROM template_version
                    JOIN test_template
                      ON test_template.id = template_version.test_template_id
                    WHERE template_version.id = test_session.template_version_id),
                template_subject_snapshot = (
                    SELECT test_template.subject
                    FROM template_version
                    JOIN test_template
                      ON test_template.id = template_version.test_template_id
                    WHERE template_version.id = test_session.template_version_id),
                template_grade_label_snapshot = (
                    SELECT test_template.grade_label
                    FROM template_version
                    JOIN test_template
                      ON test_template.id = template_version.test_template_id
                    WHERE template_version.id = test_session.template_version_id),
                template_category_snapshot = (
                    SELECT test_template.category
                    FROM template_version
                    JOIN test_template
                      ON test_template.id = template_version.test_template_id
                    WHERE template_version.id = test_session.template_version_id),
                template_course_snapshot = (
                    SELECT test_template.course
                    FROM template_version
                    JOIN test_template
                      ON test_template.id = template_version.test_template_id
                    WHERE template_version.id = test_session.template_version_id)
            WHERE template_title_snapshot IS NULL;
            """);

        migrationBuilder.CreateIndex(
            name: "ux_test_session_template_publish",
            table: "test_session",
            column: "template_version_id",
            unique: true,
            filter: "\"creation_source\" = 'template_publish'");

        migrationBuilder.CreateIndex(
            name: "IX_test_session_created_by_staff_user_id_request_idempotency_key",
            table: "test_session",
            columns: new[]
            {
                "created_by_staff_user_id",
                "request_idempotency_key",
            },
            unique: true,
            filter: "\"request_idempotency_key\" IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ux_test_session_template_publish",
            table: "test_session");
        migrationBuilder.DropIndex(
            name: "IX_test_session_created_by_staff_user_id_request_idempotency_key",
            table: "test_session");

        // EF's SQLite DropColumn translation rebuilds test_session and can
        // silently discard independently managed/sentinel triggers. SQLite
        // supports native DROP COLUMN for these additive, unreferenced fields.
        migrationBuilder.Sql(
            "ALTER TABLE \"test_session\" DROP COLUMN \"creation_source\";");
        migrationBuilder.Sql(
            "ALTER TABLE \"test_session\" DROP COLUMN \"template_title_snapshot\";");
        migrationBuilder.Sql(
            "ALTER TABLE \"test_session\" DROP COLUMN \"template_subject_snapshot\";");
        migrationBuilder.Sql(
            "ALTER TABLE \"test_session\" DROP COLUMN \"template_grade_label_snapshot\";");
        migrationBuilder.Sql(
            "ALTER TABLE \"test_session\" DROP COLUMN \"template_category_snapshot\";");
        migrationBuilder.Sql(
            "ALTER TABLE \"test_session\" DROP COLUMN \"template_course_snapshot\";");
        migrationBuilder.Sql(
            "ALTER TABLE \"test_session\" DROP COLUMN \"request_idempotency_key\";");
        migrationBuilder.Sql(
            "ALTER TABLE \"test_session\" DROP COLUMN \"request_fingerprint\";");
    }
}
