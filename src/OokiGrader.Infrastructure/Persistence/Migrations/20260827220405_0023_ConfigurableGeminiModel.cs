using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OokiGrader.Infrastructure.Persistence;

#nullable disable

namespace OokiGrader.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OokiGraderDbContext))]
[Migration("20260827220405_0023_ConfigurableGeminiModel")]
public partial class _0023_ConfigurableGeminiModel : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        RebuildAiBatchTable(migrationBuilder, constrainModel: false);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        RebuildAiBatchTable(migrationBuilder, constrainModel: true);
    }

    private static void RebuildAiBatchTable(MigrationBuilder migrationBuilder, bool constrainModel)
    {
        // SQLite cannot add or remove a CHECK constraint in place. Rebuild the parent table
        // with foreign-key enforcement temporarily disabled so existing batch requests and
        // all ledger rows survive the schema-only change.
        migrationBuilder.Sql("PRAGMA foreign_keys = OFF;", suppressTransaction: true);

        var modelConstraint = constrainModel
            ? "CONSTRAINT \"ck_ai_batch_model\" CHECK (model_id = 'gemini-3.5-flash-lite'),"
            : string.Empty;

        migrationBuilder.Sql($$"""
            CREATE TABLE "ai_batch__0023" (
                "id" TEXT NOT NULL CONSTRAINT "PK_ai_batch" PRIMARY KEY,
                "provider" TEXT NOT NULL,
                "model_id" TEXT NOT NULL,
                "ai_connection_id" TEXT NOT NULL,
                "connection_revision" INTEGER NOT NULL,
                "ai_task_profile_id" TEXT NOT NULL,
                "task_profile_revision" INTEGER NOT NULL,
                "compatibility_key" TEXT NOT NULL,
                "manifest_json" TEXT NOT NULL,
                "manifest_hash" TEXT NOT NULL,
                "display_name" TEXT NOT NULL,
                "state" TEXT NOT NULL,
                "submission_epoch" INTEGER NOT NULL,
                "create_attempt_count" INTEGER NOT NULL,
                "create_attempt_started_at" INTEGER NULL,
                "create_attempt_completed_at" INTEGER NULL,
                "provider_batch_name" TEXT NULL,
                "provider_input_file_name" TEXT NULL,
                "provider_output_file_name" TEXT NULL,
                "provider_input_file_expires_at" INTEGER NULL,
                "input_json_lines_sha256" TEXT NULL,
                "input_json_lines_bytes" INTEGER NOT NULL,
                "request_count" INTEGER NOT NULL,
                "successful_request_count" INTEGER NOT NULL,
                "failed_request_count" INTEGER NOT NULL,
                "pending_request_count" INTEGER NOT NULL,
                "possible_duplicate" INTEGER NOT NULL,
                "reconciliation_attempt_count" INTEGER NOT NULL,
                "reconciliation_deadline_at" INTEGER NULL,
                "last_polled_at" INTEGER NULL,
                "next_action_at" INTEGER NULL,
                "remote_created_at" INTEGER NULL,
                "remote_updated_at" INTEGER NULL,
                "remote_ended_at" INTEGER NULL,
                "error_code" TEXT NULL,
                "safe_error_detail" TEXT NULL,
                "cleanup_state" TEXT NOT NULL,
                "created_at" INTEGER NOT NULL,
                "updated_at" INTEGER NOT NULL,
                "completed_at" INTEGER NULL,
                "revision" INTEGER NOT NULL,
                CONSTRAINT "ck_ai_batch_cleanup" CHECK (cleanup_state IN ('not_started','pending','completed','failed','expired')),
                CONSTRAINT "ck_ai_batch_counts" CHECK (submission_epoch > 0 AND create_attempt_count >= 0 AND request_count > 0 AND input_json_lines_bytes >= 0 AND successful_request_count >= 0 AND failed_request_count >= 0 AND pending_request_count >= 0 AND reconciliation_attempt_count >= 0),
                CONSTRAINT "ck_ai_batch_create_attempt" CHECK (create_attempt_count <= 1),
                {{modelConstraint}}
                CONSTRAINT "ck_ai_batch_provider" CHECK (provider = 'geminiDirect'),
                CONSTRAINT "ck_ai_batch_remote_identity" CHECK ((provider_batch_name IS NULL) OR (state NOT IN ('prepared','uploading','submitting','reconcile_required'))),
                CONSTRAINT "ck_ai_batch_state" CHECK (state IN ('prepared','uploading','submitting','submitted','reconcile_required','pending','running','delayed','succeeded','failed','cancelled','expired','manual_review')),
                CONSTRAINT "FK_ai_batch_ai_connection_ai_connection_id" FOREIGN KEY ("ai_connection_id") REFERENCES "ai_connection" ("id") ON DELETE RESTRICT,
                CONSTRAINT "FK_ai_batch_ai_task_profile_ai_task_profile_id" FOREIGN KEY ("ai_task_profile_id") REFERENCES "ai_task_profile" ("id") ON DELETE RESTRICT
            );

            INSERT INTO "ai_batch__0023" (
                "id", "provider", "model_id", "ai_connection_id", "connection_revision",
                "ai_task_profile_id", "task_profile_revision", "compatibility_key", "manifest_json",
                "manifest_hash", "display_name", "state", "submission_epoch", "create_attempt_count",
                "create_attempt_started_at", "create_attempt_completed_at", "provider_batch_name",
                "provider_input_file_name", "provider_output_file_name", "provider_input_file_expires_at",
                "input_json_lines_sha256", "input_json_lines_bytes", "request_count",
                "successful_request_count", "failed_request_count", "pending_request_count",
                "possible_duplicate", "reconciliation_attempt_count", "reconciliation_deadline_at",
                "last_polled_at", "next_action_at", "remote_created_at", "remote_updated_at",
                "remote_ended_at", "error_code", "safe_error_detail", "cleanup_state", "created_at",
                "updated_at", "completed_at", "revision")
            SELECT
                "id", "provider", "model_id", "ai_connection_id", "connection_revision",
                "ai_task_profile_id", "task_profile_revision", "compatibility_key", "manifest_json",
                "manifest_hash", "display_name", "state", "submission_epoch", "create_attempt_count",
                "create_attempt_started_at", "create_attempt_completed_at", "provider_batch_name",
                "provider_input_file_name", "provider_output_file_name", "provider_input_file_expires_at",
                "input_json_lines_sha256", "input_json_lines_bytes", "request_count",
                "successful_request_count", "failed_request_count", "pending_request_count",
                "possible_duplicate", "reconciliation_attempt_count", "reconciliation_deadline_at",
                "last_polled_at", "next_action_at", "remote_created_at", "remote_updated_at",
                "remote_ended_at", "error_code", "safe_error_detail", "cleanup_state", "created_at",
                "updated_at", "completed_at", "revision"
            FROM "ai_batch";

            DROP TABLE "ai_batch";
            ALTER TABLE "ai_batch__0023" RENAME TO "ai_batch";

            CREATE INDEX "IX_ai_batch_ai_connection_id" ON "ai_batch" ("ai_connection_id");
            CREATE INDEX "IX_ai_batch_ai_task_profile_id" ON "ai_batch" ("ai_task_profile_id");
            CREATE INDEX "IX_ai_batch_compatibility_key_state" ON "ai_batch" ("compatibility_key", "state");
            CREATE UNIQUE INDEX "IX_ai_batch_display_name" ON "ai_batch" ("display_name");
            CREATE UNIQUE INDEX "IX_ai_batch_provider_batch_name" ON "ai_batch" ("provider_batch_name") WHERE "provider_batch_name" IS NOT NULL;
            CREATE INDEX "IX_ai_batch_state_next_action_at_created_at" ON "ai_batch" ("state", "next_action_at", "created_at");
            """);

        migrationBuilder.Sql("PRAGMA foreign_key_check;", suppressTransaction: true);
        migrationBuilder.Sql("PRAGMA foreign_keys = ON;", suppressTransaction: true);
    }
}
