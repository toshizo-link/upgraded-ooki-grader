using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OokiGrader.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0009_GeminiAiCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_budget_policy",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    daily_warning_usd_micros = table.Column<long>(type: "INTEGER", nullable: false),
                    daily_hard_usd_micros = table.Column<long>(type: "INTEGER", nullable: false),
                    monthly_warning_usd_micros = table.Column<long>(type: "INTEGER", nullable: false),
                    monthly_hard_usd_micros = table.Column<long>(type: "INTEGER", nullable: false),
                    usd_to_jpy_micros = table.Column<long>(type: "INTEGER", nullable: false),
                    active = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_budget_policy", x => x.id);
                    table.CheckConstraint("ck_ai_budget_policy_limits", "daily_warning_usd_micros >= 0 AND daily_hard_usd_micros >= daily_warning_usd_micros AND monthly_warning_usd_micros >= 0 AND monthly_hard_usd_micros >= monthly_warning_usd_micros AND usd_to_jpy_micros > 0");
                });

            migrationBuilder.CreateTable(
                name: "ai_connection",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    provider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    endpoint_profile = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    model_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    secret_reference = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    key_fingerprint = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    credential_revision = table.Column<int>(type: "INTEGER", nullable: false),
                    timeout_seconds = table.Column<int>(type: "INTEGER", nullable: false),
                    concurrency_limit = table.Column<int>(type: "INTEGER", nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    last_capability_probe_state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    last_capability_probe_error_code = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    last_capability_probe_at = table.Column<long>(type: "INTEGER", nullable: true),
                    created_by_staff_user_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_connection", x => x.id);
                    table.CheckConstraint("ck_ai_connection_limits", "credential_revision > 0 AND timeout_seconds BETWEEN 5 AND 300 AND concurrency_limit BETWEEN 1 AND 16");
                    table.CheckConstraint("ck_ai_connection_provider", "provider = 'geminiDirect'");
                    table.CheckConstraint("ck_ai_connection_state", "state IN ('pending_probe','active','disabled','blocked')");
                });

            migrationBuilder.CreateTable(
                name: "pricing_snapshot",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    provider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    model_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    input_usd_micros_per_million_tokens = table.Column<long>(type: "INTEGER", nullable: false),
                    output_usd_micros_per_million_tokens = table.Column<long>(type: "INTEGER", nullable: false),
                    thinking_usd_micros_per_million_tokens = table.Column<long>(type: "INTEGER", nullable: false),
                    source_url = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    effective_at = table.Column<long>(type: "INTEGER", nullable: false),
                    captured_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pricing_snapshot", x => x.id);
                    table.CheckConstraint("ck_pricing_snapshot_rates", "input_usd_micros_per_million_tokens >= 0 AND output_usd_micros_per_million_tokens >= 0 AND thinking_usd_micros_per_million_tokens >= 0");
                });

            migrationBuilder.CreateTable(
                name: "ai_capability_probe",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    ai_connection_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    connection_revision = table.Column<int>(type: "INTEGER", nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    authentication = table.Column<bool>(type: "INTEGER", nullable: false),
                    model_available = table.Column<bool>(type: "INTEGER", nullable: false),
                    image_input = table.Column<bool>(type: "INTEGER", nullable: false),
                    structured_output = table.Column<bool>(type: "INTEGER", nullable: false),
                    usage_metadata = table.Column<bool>(type: "INTEGER", nullable: false),
                    safe_error_code = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    latency_milliseconds = table.Column<long>(type: "INTEGER", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    completed_at = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_capability_probe", x => x.id);
                    table.CheckConstraint("ck_ai_capability_probe_latency", "latency_milliseconds IS NULL OR latency_milliseconds >= 0");
                    table.CheckConstraint("ck_ai_capability_probe_state", "state IN ('running','passed','failed')");
                    table.ForeignKey(
                        name: "FK_ai_capability_probe_ai_connection_ai_connection_id",
                        column: x => x.ai_connection_id,
                        principalTable: "ai_connection",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ai_task_profile",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    task_type = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ai_connection_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    connection_revision = table.Column<long>(type: "INTEGER", nullable: false),
                    model_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    processing_strategy = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    prompt_version = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    schema_version = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    prompt_content_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    thinking_level = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    media_resolution = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    max_output_tokens = table.Column<int>(type: "INTEGER", nullable: false),
                    concurrency_limit = table.Column<int>(type: "INTEGER", nullable: false),
                    approval_state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    accuracy_evaluation_id = table.Column<string>(type: "TEXT", nullable: true),
                    active = table.Column<bool>(type: "INTEGER", nullable: false),
                    activated_at = table.Column<long>(type: "INTEGER", nullable: true),
                    activated_by_staff_user_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                    created_by_staff_user_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_task_profile", x => x.id);
                    table.CheckConstraint("ck_ai_task_profile_approval", "approval_state IN ('draft','capability_passed','pilot_approved','production_approved','rejected')");
                    table.CheckConstraint("ck_ai_task_profile_limits", "max_output_tokens BETWEEN 64 AND 65536 AND concurrency_limit BETWEEN 1 AND 16");
                    table.CheckConstraint("ck_ai_task_profile_strategy", "processing_strategy IN ('gemini_batch','queued_standard','expedite_standard')");
                    table.CheckConstraint("ck_ai_task_profile_task", "task_type IN ('templateExtraction','nameTranscription','initialGrading','adjudication')");
                    table.ForeignKey(
                        name: "FK_ai_task_profile_ai_connection_ai_connection_id",
                        column: x => x.ai_connection_id,
                        principalTable: "ai_connection",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ai_request",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    request_key = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ai_task_profile_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    task_profile_revision = table.Column<long>(type: "INTEGER", nullable: false),
                    purpose = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    entity_type = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    entity_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    entity_revision = table.Column<long>(type: "INTEGER", nullable: false),
                    input_manifest_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    dispatch_attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    possible_duplicate = table.Column<bool>(type: "INTEGER", nullable: false),
                    provider_response_id = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    actual_model = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    finish_reason = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    accepted_response_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    validated_response_json = table.Column<string>(type: "TEXT", maxLength: 1000000, nullable: true),
                    error_code = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    safe_error_detail = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    dispatched_at = table.Column<long>(type: "INTEGER", nullable: true),
                    completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_request", x => x.id);
                    table.CheckConstraint("ck_ai_request_attempt", "dispatch_attempt >= 0");
                    table.CheckConstraint("ck_ai_request_state", "state IN ('prepared','budget_blocked','dispatching','retry_waiting','succeeded','invalid_output','safety_blocked','failed','cancelled')");
                    table.ForeignKey(
                        name: "FK_ai_request_ai_task_profile_ai_task_profile_id",
                        column: x => x.ai_task_profile_id,
                        principalTable: "ai_task_profile",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ai_budget_reservation",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    ai_request_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    usage_day = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    usage_month = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    reserved_usd_micros = table.Column<long>(type: "INTEGER", nullable: false),
                    actual_usd_micros = table.Column<long>(type: "INTEGER", nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    settled_at = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_budget_reservation", x => x.id);
                    table.CheckConstraint("ck_ai_budget_reservation_amounts", "reserved_usd_micros >= 0 AND actual_usd_micros >= 0");
                    table.CheckConstraint("ck_ai_budget_reservation_state", "state IN ('reserved','settled','released')");
                    table.ForeignKey(
                        name: "FK_ai_budget_reservation_ai_request_ai_request_id",
                        column: x => x.ai_request_id,
                        principalTable: "ai_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ai_usage",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    ai_request_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    requested_provider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    requested_model = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    actual_provider = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    actual_model = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    input_tokens = table.Column<int>(type: "INTEGER", nullable: true),
                    cached_tokens = table.Column<int>(type: "INTEGER", nullable: true),
                    output_tokens = table.Column<int>(type: "INTEGER", nullable: true),
                    thinking_tokens = table.Column<int>(type: "INTEGER", nullable: true),
                    total_tokens = table.Column<int>(type: "INTEGER", nullable: true),
                    pricing_snapshot_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                    estimated_usd_micros = table.Column<long>(type: "INTEGER", nullable: false),
                    estimated_jpy_micros = table.Column<long>(type: "INTEGER", nullable: false),
                    provider_request_id = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    measured_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_usage", x => x.id);
                    table.CheckConstraint("ck_ai_usage_cost", "estimated_usd_micros >= 0 AND estimated_jpy_micros >= 0");
                    table.CheckConstraint("ck_ai_usage_tokens", "(input_tokens IS NULL OR input_tokens >= 0) AND (cached_tokens IS NULL OR cached_tokens >= 0) AND (output_tokens IS NULL OR output_tokens >= 0) AND (thinking_tokens IS NULL OR thinking_tokens >= 0) AND (total_tokens IS NULL OR total_tokens >= 0)");
                    table.ForeignKey(
                        name: "FK_ai_usage_ai_request_ai_request_id",
                        column: x => x.ai_request_id,
                        principalTable: "ai_request",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ai_usage_pricing_snapshot_pricing_snapshot_id",
                        column: x => x.pricing_snapshot_id,
                        principalTable: "pricing_snapshot",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_budget_reservation_ai_request_id",
                table: "ai_budget_reservation",
                column: "ai_request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_budget_reservation_usage_day_state",
                table: "ai_budget_reservation",
                columns: new[] { "usage_day", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_budget_reservation_usage_month_state",
                table: "ai_budget_reservation",
                columns: new[] { "usage_month", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_capability_probe_ai_connection_id_created_at",
                table: "ai_capability_probe",
                columns: new[] { "ai_connection_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ai_connection_provider_state",
                table: "ai_connection",
                columns: new[] { "provider", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_request_ai_task_profile_id",
                table: "ai_request",
                column: "ai_task_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_ai_request_entity_type_entity_id_input_manifest_hash_task_profile_revision",
                table: "ai_request",
                columns: new[] { "entity_type", "entity_id", "input_manifest_hash", "task_profile_revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_request_request_key",
                table: "ai_request",
                column: "request_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_request_state_created_at",
                table: "ai_request",
                columns: new[] { "state", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_task_profile_ai_connection_id",
                table: "ai_task_profile",
                column: "ai_connection_id");

            migrationBuilder.CreateIndex(
                name: "IX_ai_task_profile_task_type_active",
                table: "ai_task_profile",
                columns: new[] { "task_type", "active" },
                unique: true,
                filter: "\"active\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_ai_usage_ai_request_id",
                table: "ai_usage",
                column: "ai_request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_usage_measured_at",
                table: "ai_usage",
                column: "measured_at");

            migrationBuilder.CreateIndex(
                name: "IX_ai_usage_pricing_snapshot_id",
                table: "ai_usage",
                column: "pricing_snapshot_id");

            migrationBuilder.CreateIndex(
                name: "IX_pricing_snapshot_provider_model_id_effective_at",
                table: "pricing_snapshot",
                columns: new[] { "provider", "model_id", "effective_at" },
                descending: new[] { false, false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_budget_policy");

            migrationBuilder.DropTable(
                name: "ai_budget_reservation");

            migrationBuilder.DropTable(
                name: "ai_capability_probe");

            migrationBuilder.DropTable(
                name: "ai_usage");

            migrationBuilder.DropTable(
                name: "ai_request");

            migrationBuilder.DropTable(
                name: "pricing_snapshot");

            migrationBuilder.DropTable(
                name: "ai_task_profile");

            migrationBuilder.DropTable(
                name: "ai_connection");
        }
    }
}
