using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OokiGrader.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0011_GeminiBatchLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_request_state",
                table: "ai_request");

            migrationBuilder.CreateTable(
                name: "ai_batch",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    provider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    model_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ai_connection_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    connection_revision = table.Column<long>(type: "INTEGER", nullable: false),
                    ai_task_profile_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    task_profile_revision = table.Column<long>(type: "INTEGER", nullable: false),
                    compatibility_key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    manifest_json = table.Column<string>(type: "TEXT", maxLength: 1000000, nullable: false),
                    manifest_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    display_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    submission_epoch = table.Column<int>(type: "INTEGER", nullable: false),
                    create_attempt_count = table.Column<int>(type: "INTEGER", nullable: false),
                    create_attempt_started_at = table.Column<long>(type: "INTEGER", nullable: true),
                    create_attempt_completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    provider_batch_name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    provider_input_file_name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    provider_output_file_name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    provider_input_file_expires_at = table.Column<long>(type: "INTEGER", nullable: true),
                    input_json_lines_sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    input_json_lines_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    request_count = table.Column<int>(type: "INTEGER", nullable: false),
                    successful_request_count = table.Column<long>(type: "INTEGER", nullable: false),
                    failed_request_count = table.Column<long>(type: "INTEGER", nullable: false),
                    pending_request_count = table.Column<long>(type: "INTEGER", nullable: false),
                    possible_duplicate = table.Column<bool>(type: "INTEGER", nullable: false),
                    reconciliation_attempt_count = table.Column<int>(type: "INTEGER", nullable: false),
                    reconciliation_deadline_at = table.Column<long>(type: "INTEGER", nullable: true),
                    last_polled_at = table.Column<long>(type: "INTEGER", nullable: true),
                    next_action_at = table.Column<long>(type: "INTEGER", nullable: true),
                    remote_created_at = table.Column<long>(type: "INTEGER", nullable: true),
                    remote_updated_at = table.Column<long>(type: "INTEGER", nullable: true),
                    remote_ended_at = table.Column<long>(type: "INTEGER", nullable: true),
                    error_code = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    safe_error_detail = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    cleanup_state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_batch", x => x.id);
                    table.CheckConstraint("ck_ai_batch_cleanup", "cleanup_state IN ('not_started','pending','completed','failed','expired')");
                    table.CheckConstraint("ck_ai_batch_counts", "submission_epoch > 0 AND create_attempt_count >= 0 AND request_count > 0 AND input_json_lines_bytes >= 0 AND successful_request_count >= 0 AND failed_request_count >= 0 AND pending_request_count >= 0 AND reconciliation_attempt_count >= 0");
                    table.CheckConstraint("ck_ai_batch_create_attempt", "create_attempt_count <= 1");
                    table.CheckConstraint("ck_ai_batch_model", "model_id = 'gemini-3.5-flash-lite'");
                    table.CheckConstraint("ck_ai_batch_provider", "provider = 'geminiDirect'");
                    table.CheckConstraint("ck_ai_batch_remote_identity", "(provider_batch_name IS NULL) OR (state NOT IN ('prepared','uploading','submitting','reconcile_required'))");
                    table.CheckConstraint("ck_ai_batch_state", "state IN ('prepared','uploading','submitting','submitted','reconcile_required','pending','running','delayed','succeeded','failed','cancelled','expired','manual_review')");
                    table.ForeignKey(
                        name: "FK_ai_batch_ai_connection_ai_connection_id",
                        column: x => x.ai_connection_id,
                        principalTable: "ai_connection",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ai_batch_ai_task_profile_ai_task_profile_id",
                        column: x => x.ai_task_profile_id,
                        principalTable: "ai_task_profile",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ai_batch_request",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    ai_batch_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                    ai_request_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    request_key = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    compatibility_key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    provider_request_json = table.Column<string>(type: "TEXT", maxLength: 25000000, nullable: true),
                    provider_request_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    provider_request_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ordinal = table.Column<int>(type: "INTEGER", nullable: true),
                    state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    provider_response_id = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    response_json = table.Column<string>(type: "TEXT", maxLength: 1000000, nullable: true),
                    response_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    error_code = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_batch_request", x => x.id);
                    table.CheckConstraint("ck_ai_batch_request_bytes", "((state IN ('ready','prepared','submitted') AND provider_request_json IS NOT NULL AND provider_request_bytes > 0) OR (state IN ('response_ready','failed','missing','cancelled') AND provider_request_json IS NULL AND provider_request_bytes = 0))");
                    table.CheckConstraint("ck_ai_batch_request_ordinal", "(ai_batch_id IS NULL AND ordinal IS NULL) OR (ai_batch_id IS NOT NULL AND ordinal >= 0)");
                    table.CheckConstraint("ck_ai_batch_request_state", "state IN ('ready','prepared','submitted','response_ready','failed','missing','cancelled')");
                    table.ForeignKey(
                        name: "FK_ai_batch_request_ai_batch_ai_batch_id",
                        column: x => x.ai_batch_id,
                        principalTable: "ai_batch",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ai_batch_request_ai_request_ai_request_id",
                        column: x => x.ai_request_id,
                        principalTable: "ai_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_request_state",
                table: "ai_request",
                sql: "state IN ('prepared','budget_blocked','dispatching','retry_waiting','response_ready','succeeded','invalid_output','safety_blocked','failed','cancelled')");

            migrationBuilder.CreateIndex(
                name: "IX_ai_batch_ai_connection_id",
                table: "ai_batch",
                column: "ai_connection_id");

            migrationBuilder.CreateIndex(
                name: "IX_ai_batch_ai_task_profile_id",
                table: "ai_batch",
                column: "ai_task_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_ai_batch_compatibility_key_state",
                table: "ai_batch",
                columns: new[] { "compatibility_key", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_batch_display_name",
                table: "ai_batch",
                column: "display_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_batch_provider_batch_name",
                table: "ai_batch",
                column: "provider_batch_name",
                unique: true,
                filter: "\"provider_batch_name\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ai_batch_state_next_action_at_created_at",
                table: "ai_batch",
                columns: new[] { "state", "next_action_at", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_batch_request_ai_batch_id_ordinal",
                table: "ai_batch_request",
                columns: new[] { "ai_batch_id", "ordinal" },
                unique: true,
                filter: "\"ai_batch_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ai_batch_request_ai_request_id",
                table: "ai_batch_request",
                column: "ai_request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_batch_request_request_key",
                table: "ai_batch_request",
                column: "request_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_batch_request_state_compatibility_key_created_at",
                table: "ai_batch_request",
                columns: new[] { "state", "compatibility_key", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_batch_request");

            migrationBuilder.DropTable(
                name: "ai_batch");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_request_state",
                table: "ai_request");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_request_state",
                table: "ai_request",
                sql: "state IN ('prepared','budget_blocked','dispatching','retry_waiting','succeeded','invalid_output','safety_blocked','failed','cancelled')");
        }
    }
}
