using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OokiGrader.Infrastructure.Persistence;

#nullable disable

namespace OokiGrader.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OokiGraderDbContext))]
[Migration("20260805000000_0015_NonModelAnswerTemplateSourceRole")]
public partial class _0015_NonModelAnswerTemplateSourceRole : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        RebuildTemplateSourceTable(migrationBuilder, allowNonModelRole: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        RebuildTemplateSourceTable(migrationBuilder, allowNonModelRole: false);
    }

    private static void RebuildTemplateSourceTable(
        MigrationBuilder migrationBuilder,
        bool allowNonModelRole)
    {
        // SQLite cannot alter a CHECK constraint in place. Rebuilding this
        // small metadata table preserves rows, foreign keys, and indexes.
        if (allowNonModelRole)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE "template_source_new" (
                    "id" TEXT NOT NULL CONSTRAINT "PK_template_source" PRIMARY KEY,
                    "template_version_id" TEXT NOT NULL,
                    "upload_session_id" TEXT NOT NULL,
                    "file_reference_id" TEXT NULL,
                    "source_role" TEXT NOT NULL,
                    "display_name" TEXT NOT NULL,
                    "ordinal" INTEGER NOT NULL,
                    "uploaded_by_staff_user_id" TEXT NOT NULL,
                    "created_at" INTEGER NOT NULL,
                    CONSTRAINT "ck_template_source_ordinal"
                        CHECK (ordinal >= 0),
                    CONSTRAINT "ck_template_source_role"
                        CHECK (source_role IN ('blank_test',
                            'contains_model_answers',
                            'contains_non_model_answers',
                            'separate_answer_key')),
                    CONSTRAINT "FK_template_source_template_version_template_version_id"
                        FOREIGN KEY ("template_version_id")
                        REFERENCES "template_version" ("id") ON DELETE RESTRICT,
                    CONSTRAINT "FK_template_source_upload_session_upload_session_id"
                        FOREIGN KEY ("upload_session_id")
                        REFERENCES "upload_session" ("id") ON DELETE RESTRICT
                );
                """);
        }
        else
        {
            migrationBuilder.Sql(
                """
            CREATE TABLE "template_source_new" (
                "id" TEXT NOT NULL CONSTRAINT "PK_template_source" PRIMARY KEY,
                "template_version_id" TEXT NOT NULL,
                "upload_session_id" TEXT NOT NULL,
                "file_reference_id" TEXT NULL,
                "source_role" TEXT NOT NULL,
                "display_name" TEXT NOT NULL,
                "ordinal" INTEGER NOT NULL,
                "uploaded_by_staff_user_id" TEXT NOT NULL,
                "created_at" INTEGER NOT NULL,
                CONSTRAINT "ck_template_source_ordinal"
                    CHECK (ordinal >= 0),
                CONSTRAINT "ck_template_source_role"
                    CHECK (source_role IN ('blank_test',
                        'contains_model_answers', 'separate_answer_key')),
                CONSTRAINT "FK_template_source_template_version_template_version_id"
                    FOREIGN KEY ("template_version_id")
                    REFERENCES "template_version" ("id") ON DELETE RESTRICT,
                CONSTRAINT "FK_template_source_upload_session_upload_session_id"
                    FOREIGN KEY ("upload_session_id")
                    REFERENCES "upload_session" ("id") ON DELETE RESTRICT
            );
            """);
        }

        if (allowNonModelRole)
        {
            migrationBuilder.Sql(
                """
                INSERT INTO "template_source_new" (
                    "id", "template_version_id", "upload_session_id",
                    "file_reference_id", "source_role", "display_name",
                    "ordinal", "uploaded_by_staff_user_id", "created_at")
                SELECT
                    "id", "template_version_id", "upload_session_id",
                    "file_reference_id", "source_role", "display_name",
                    "ordinal", "uploaded_by_staff_user_id", "created_at"
                FROM "template_source";
                """);
        }
        else
        {
            migrationBuilder.Sql(
                """
            INSERT INTO "template_source_new" (
                "id", "template_version_id", "upload_session_id",
                "file_reference_id", "source_role", "display_name",
                "ordinal", "uploaded_by_staff_user_id", "created_at")
            SELECT
                "id", "template_version_id", "upload_session_id",
                "file_reference_id",
                CASE WHEN "source_role" = 'contains_non_model_answers'
                    THEN 'blank_test' ELSE "source_role" END,
                "display_name",
                "ordinal", "uploaded_by_staff_user_id", "created_at"
            FROM "template_source";
            """);
        }

        migrationBuilder.Sql("DROP TABLE \"template_source\";");
        migrationBuilder.Sql(
            "ALTER TABLE \"template_source_new\" " +
            "RENAME TO \"template_source\";");

        migrationBuilder.Sql(
            """
            CREATE UNIQUE INDEX
                "IX_template_source_template_version_id_ordinal"
                ON "template_source" ("template_version_id", "ordinal");
            """);
        migrationBuilder.Sql(
            """
            CREATE INDEX "IX_template_source_upload_session_id"
                ON "template_source" ("upload_session_id");
            """);
    }
}
