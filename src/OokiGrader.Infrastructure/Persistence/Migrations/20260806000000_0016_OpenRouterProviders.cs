using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OokiGrader.Infrastructure.Persistence;

#nullable disable

namespace OokiGrader.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OokiGraderDbContext))]
[Migration("20260806000000_0016_OpenRouterProviders")]
public partial class _0016_OpenRouterProviders : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        RebuildProviderTables(
            migrationBuilder,
            "provider IN ('geminiDirect','openRouter')",
            addOneEnabledConnectionPerProviderIndex: true);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        RebuildProviderTables(
            migrationBuilder,
            "provider = 'geminiDirect'",
            addOneEnabledConnectionPerProviderIndex: false);

    private static void RebuildProviderTables(
        MigrationBuilder migrationBuilder,
        string providerConstraint,
        bool addOneEnabledConnectionPerProviderIndex)
    {
        // SQLite cannot alter CHECK constraints. Foreign keys must be disabled
        // outside the migration transaction while the referenced connection
        // table is replaced; they are validated again before completion.
        migrationBuilder.Sql(
            "PRAGMA foreign_keys = OFF;",
            suppressTransaction: true);

        migrationBuilder.Sql(
            $$"""
            CREATE TABLE "ai_connection_new" (
                "id" TEXT NOT NULL CONSTRAINT "PK_ai_connection" PRIMARY KEY,
                "provider" TEXT NOT NULL,
                "endpoint_profile" TEXT NOT NULL,
                "model_id" TEXT NOT NULL,
                "secret_reference" TEXT NOT NULL,
                "key_fingerprint" TEXT NOT NULL,
                "credential_revision" INTEGER NOT NULL,
                "timeout_seconds" INTEGER NOT NULL,
                "concurrency_limit" INTEGER NOT NULL,
                "state" TEXT NOT NULL,
                "last_capability_probe_state" TEXT NULL,
                "last_capability_probe_error_code" TEXT NULL,
                "last_capability_probe_at" INTEGER NULL,
                "last_batch_capability_probe_state" TEXT NULL,
                "last_batch_capability_probe_error_code" TEXT NULL,
                "last_batch_capability_probe_at" INTEGER NULL,
                "last_batch_capability_probe_credential_revision" INTEGER NULL,
                "created_by_staff_user_id" TEXT NOT NULL,
                "created_at" INTEGER NOT NULL,
                "updated_at" INTEGER NOT NULL,
                "revision" INTEGER NOT NULL,
                CONSTRAINT "ck_ai_connection_provider"
                    CHECK ({{providerConstraint}}),
                CONSTRAINT "ck_ai_connection_state"
                    CHECK (state IN ('pending_probe','active','disabled','blocked')),
                CONSTRAINT "ck_ai_connection_limits"
                    CHECK (credential_revision > 0
                        AND timeout_seconds BETWEEN 5 AND 300
                        AND concurrency_limit BETWEEN 1 AND 16),
                CONSTRAINT "ck_ai_connection_batch_probe_revision"
                    CHECK (last_batch_capability_probe_credential_revision IS NULL
                        OR last_batch_capability_probe_credential_revision > 0)
            );

            INSERT INTO "ai_connection_new" (
                "id", "provider", "endpoint_profile", "model_id",
                "secret_reference", "key_fingerprint", "credential_revision",
                "timeout_seconds", "concurrency_limit", "state",
                "last_capability_probe_state",
                "last_capability_probe_error_code", "last_capability_probe_at",
                "last_batch_capability_probe_state",
                "last_batch_capability_probe_error_code",
                "last_batch_capability_probe_at",
                "last_batch_capability_probe_credential_revision",
                "created_by_staff_user_id", "created_at", "updated_at", "revision")
            SELECT
                "id", "provider", "endpoint_profile", "model_id",
                "secret_reference", "key_fingerprint", "credential_revision",
                "timeout_seconds", "concurrency_limit", "state",
                "last_capability_probe_state",
                "last_capability_probe_error_code", "last_capability_probe_at",
                "last_batch_capability_probe_state",
                "last_batch_capability_probe_error_code",
                "last_batch_capability_probe_at",
                "last_batch_capability_probe_credential_revision",
                "created_by_staff_user_id", "created_at", "updated_at", "revision"
            FROM "ai_connection";

            DROP TABLE "ai_connection";
            ALTER TABLE "ai_connection_new" RENAME TO "ai_connection";
            CREATE INDEX "IX_ai_connection_provider_state"
                ON "ai_connection" ("provider", "state");
            """);

        if (addOneEnabledConnectionPerProviderIndex)
        {
            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX "IX_ai_connection_provider"
                    ON "ai_connection" ("provider")
                    WHERE "state" <> 'disabled';
                """);
        }

        migrationBuilder.Sql(
            $$"""
            CREATE TABLE "ai_evaluation_record_new" (
                "id" TEXT NOT NULL
                    CONSTRAINT "PK_ai_evaluation_record" PRIMARY KEY,
                "ai_task_profile_id" TEXT NOT NULL,
                "task_profile_revision" INTEGER NOT NULL,
                "provider" TEXT NOT NULL,
                "model_id" TEXT NOT NULL,
                "connection_revision" INTEGER NOT NULL,
                "task_type" TEXT NOT NULL,
                "processing_strategy" TEXT NOT NULL,
                "prompt_version" TEXT NOT NULL,
                "schema_version" TEXT NOT NULL,
                "prompt_content_hash" TEXT NOT NULL,
                "dataset_version" TEXT NOT NULL,
                "dataset_sha256" TEXT NOT NULL,
                "evidence_sha256" TEXT NOT NULL,
                "sample_count" INTEGER NOT NULL,
                "agreement_basis_points" INTEGER NOT NULL,
                "lower_confidence_bound_basis_points" INTEGER NOT NULL,
                "critical_failure_count" INTEGER NOT NULL,
                "teacher_review_only" INTEGER NOT NULL,
                "signed_off_by_staff_user_id" TEXT NOT NULL,
                "created_at" INTEGER NOT NULL,
                CONSTRAINT "ck_ai_evaluation_record_provider"
                    CHECK ({{providerConstraint}}),
                CONSTRAINT "ck_ai_evaluation_record_task"
                    CHECK (task_type IN ('templateExtraction','nameTranscription',
                        'initialGrading','adjudication')),
                CONSTRAINT "ck_ai_evaluation_record_strategy"
                    CHECK (processing_strategy IN ('gemini_batch',
                        'queued_standard','expedite_standard')),
                CONSTRAINT "ck_ai_evaluation_record_revisions"
                    CHECK (task_profile_revision > 0 AND connection_revision > 0),
                CONSTRAINT "ck_ai_evaluation_record_sample"
                    CHECK (sample_count > 0 AND critical_failure_count >= 0
                        AND critical_failure_count <= sample_count),
                CONSTRAINT "ck_ai_evaluation_record_accuracy"
                    CHECK (agreement_basis_points BETWEEN 0 AND 10000
                        AND lower_confidence_bound_basis_points BETWEEN 0 AND 10000
                        AND lower_confidence_bound_basis_points
                            <= agreement_basis_points),
                CONSTRAINT "FK_ai_evaluation_record_ai_task_profile_ai_task_profile_id"
                    FOREIGN KEY ("ai_task_profile_id")
                    REFERENCES "ai_task_profile" ("id") ON DELETE RESTRICT,
                CONSTRAINT "FK_ai_evaluation_record_staff_user_signed_off_by_staff_user_id"
                    FOREIGN KEY ("signed_off_by_staff_user_id")
                    REFERENCES "staff_user" ("id") ON DELETE RESTRICT
            );

            INSERT INTO "ai_evaluation_record_new" (
                "id", "ai_task_profile_id", "task_profile_revision", "provider",
                "model_id", "connection_revision", "task_type",
                "processing_strategy", "prompt_version", "schema_version",
                "prompt_content_hash", "dataset_version", "dataset_sha256",
                "evidence_sha256", "sample_count", "agreement_basis_points",
                "lower_confidence_bound_basis_points", "critical_failure_count",
                "teacher_review_only", "signed_off_by_staff_user_id", "created_at")
            SELECT
                "id", "ai_task_profile_id", "task_profile_revision", "provider",
                "model_id", "connection_revision", "task_type",
                "processing_strategy", "prompt_version", "schema_version",
                "prompt_content_hash", "dataset_version", "dataset_sha256",
                "evidence_sha256", "sample_count", "agreement_basis_points",
                "lower_confidence_bound_basis_points", "critical_failure_count",
                "teacher_review_only", "signed_off_by_staff_user_id", "created_at"
            FROM "ai_evaluation_record";

            DROP TABLE "ai_evaluation_record";
            ALTER TABLE "ai_evaluation_record_new"
                RENAME TO "ai_evaluation_record";
            CREATE INDEX
                "IX_ai_evaluation_record_ai_task_profile_id_created_at"
                ON "ai_evaluation_record" ("ai_task_profile_id", "created_at" DESC);
            CREATE UNIQUE INDEX
                "IX_ai_evaluation_record_ai_task_profile_id_task_profile_revision_evidence_sha256"
                ON "ai_evaluation_record" (
                    "ai_task_profile_id", "task_profile_revision", "evidence_sha256");
            CREATE INDEX
                "IX_ai_evaluation_record_signed_off_by_staff_user_id"
                ON "ai_evaluation_record" ("signed_off_by_staff_user_id");
            """);

        migrationBuilder.Sql(
            "PRAGMA foreign_keys = ON;",
            suppressTransaction: true);
    }
}
