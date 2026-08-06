using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OokiGrader.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0001_InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_event",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    occurred_at = table.Column<long>(type: "INTEGER", nullable: false),
                    actor_staff_user_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                    event_type = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    object_type = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    object_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    outcome = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    reason_code = table.Column<string>(type: "TEXT", nullable: true),
                    correlation_id = table.Column<string>(type: "TEXT", nullable: true),
                    safe_metadata_json = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_event", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "background_job",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    type = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    schema_version = table.Column<int>(type: "INTEGER", nullable: false),
                    deduplication_key = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    priority = table.Column<int>(type: "INTEGER", nullable: false),
                    payload_json = table.Column<string>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    attempt_count = table.Column<int>(type: "INTEGER", nullable: false),
                    max_attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    next_attempt_at = table.Column<long>(type: "INTEGER", nullable: false),
                    lease_owner = table.Column<string>(type: "TEXT", nullable: true),
                    lease_expires_at = table.Column<long>(type: "INTEGER", nullable: true),
                    progress_basis_points = table.Column<int>(type: "INTEGER", nullable: false),
                    error_code = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    safe_error_detail = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    correlation_id = table.Column<string>(type: "TEXT", nullable: true),
                    causation_id = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    started_at = table.Column<long>(type: "INTEGER", nullable: true),
                    completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_background_job", x => x.id);
                    table.CheckConstraint("ck_background_job_attempts", "attempt_count >= 0 AND max_attempts > 0");
                    table.CheckConstraint("ck_background_job_lease", "(state <> 'leased') OR (lease_owner IS NOT NULL AND lease_expires_at IS NOT NULL)");
                    table.CheckConstraint("ck_background_job_progress", "progress_basis_points BETWEEN 0 AND 10000");
                    table.CheckConstraint("ck_background_job_state", "state IN ('queued','leased','retry_waiting','succeeded','failed','blocked','cancelled')");
                });

            migrationBuilder.CreateTable(
                name: "file_object",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    verified_mime = table.Column<string>(type: "TEXT", nullable: false),
                    extension = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    relative_object_path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    storage_class = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    retention_class = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    managed_scan_bytes = table.Column<bool>(type: "INTEGER", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    verified_at = table.Column<long>(type: "INTEGER", nullable: true),
                    deleted_at = table.Column<long>(type: "INTEGER", nullable: true),
                    reference_count_cache = table.Column<int>(type: "INTEGER", nullable: false),
                    encrypted = table.Column<bool>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_object", x => x.id);
                    table.CheckConstraint("ck_file_object_bytes", "bytes >= 0");
                    table.CheckConstraint("ck_file_object_references", "reference_count_cache >= 0");
                    table.CheckConstraint("ck_file_object_state", "state IN ('pending','available','deletion_pending','deleted','quarantined','missing')");
                });

            migrationBuilder.CreateTable(
                name: "idempotency_record",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    actor_key = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    route = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    idempotency_key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    canonical_request_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    response_status_code = table.Column<int>(type: "INTEGER", nullable: false),
                    response_content_type = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    response_body_json = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    expires_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency_record", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_event",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    aggregate_type = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    aggregate_id = table.Column<string>(type: "TEXT", nullable: false),
                    event_type = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    schema_version = table.Column<int>(type: "INTEGER", nullable: false),
                    payload_json = table.Column<string>(type: "TEXT", nullable: false),
                    correlation_id = table.Column<string>(type: "TEXT", nullable: true),
                    causation_id = table.Column<string>(type: "TEXT", nullable: true),
                    occurred_at = table.Column<long>(type: "INTEGER", nullable: false),
                    delivered_at = table.Column<long>(type: "INTEGER", nullable: true),
                    delivery_attempt_count = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_event", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "role",
                columns: table => new
                {
                    name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    display_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "site_settings",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    school_name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    time_zone = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    locale = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    managed_scan_hard_limit_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    managed_scan_cleanup_target_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    managed_scan_warning_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    physical_free_reserve_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    scan_retention_calendar_months = table.Column<int>(type: "INTEGER", nullable: false),
                    data_root = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    backup_policy_id = table.Column<string>(type: "TEXT", nullable: true),
                    active_ai_profile_set_id = table.Column<string>(type: "TEXT", nullable: true),
                    bootstrap_token_hash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    bootstrap_token_expires_at = table.Column<long>(type: "INTEGER", nullable: true),
                    bootstrap_completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    maintenance_mode = table.Column<bool>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_site_settings", x => x.id);
                    table.CheckConstraint("ck_site_settings_retention", "scan_retention_calendar_months > 0");
                    table.CheckConstraint("ck_site_settings_scan_limits", "managed_scan_warning_bytes <= managed_scan_cleanup_target_bytes AND managed_scan_cleanup_target_bytes <= managed_scan_hard_limit_bytes");
                    table.CheckConstraint("ck_site_settings_singleton", "id = 'site'");
                });

            migrationBuilder.CreateTable(
                name: "staff_user",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    username = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    username_normalized = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    display_name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    password_hash = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    password_algorithm = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    password_algorithm_version = table.Column<int>(type: "INTEGER", nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    failed_attempt_count = table.Column<int>(type: "INTEGER", nullable: false),
                    lockout_until = table.Column<long>(type: "INTEGER", nullable: true),
                    credential_changed_at = table.Column<long>(type: "INTEGER", nullable: false),
                    last_login_at = table.Column<long>(type: "INTEGER", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_user", x => x.id);
                    table.CheckConstraint("ck_staff_user_failed_attempts", "failed_attempt_count >= 0");
                    table.CheckConstraint("ck_staff_user_status", "status IN ('active','disabled')");
                });

            migrationBuilder.CreateTable(
                name: "student",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    student_number = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    student_number_normalized = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    family_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    given_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    family_name_kana = table.Column<string>(type: "TEXT", nullable: true),
                    given_name_kana = table.Column<string>(type: "TEXT", nullable: true),
                    family_name_normalized = table.Column<string>(type: "TEXT", nullable: false),
                    given_name_normalized = table.Column<string>(type: "TEXT", nullable: false),
                    family_name_kana_normalized = table.Column<string>(type: "TEXT", nullable: true),
                    given_name_kana_normalized = table.Column<string>(type: "TEXT", nullable: true),
                    display_name = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    school_class = table.Column<string>(type: "TEXT", nullable: true),
                    course = table.Column<string>(type: "TEXT", nullable: true),
                    grade_label = table.Column<string>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    merged_into_student_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                    private_notes = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_student", x => x.id);
                    table.CheckConstraint("ck_student_merge_target", "(status = 'merged' AND merged_into_student_id IS NOT NULL) OR (status <> 'merged')");
                    table.CheckConstraint("ck_student_status", "status IN ('active','inactive','merged','erasure_pending')");
                    table.ForeignKey(
                        name: "FK_student_student_merged_into_student_id",
                        column: x => x.merged_into_student_id,
                        principalTable: "student",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "file_reference",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    file_object_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    owner_type = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    owner_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    purpose = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    retention_anchor_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_reference", x => x.id);
                    table.ForeignKey(
                        name: "FK_file_reference_file_object_file_object_id",
                        column: x => x.file_object_id,
                        principalTable: "file_object",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "staff_session",
                columns: table => new
                {
                    id_hash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    staff_user_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    last_seen_at = table.Column<long>(type: "INTEGER", nullable: false),
                    absolute_expires_at = table.Column<long>(type: "INTEGER", nullable: false),
                    idle_expires_at = table.Column<long>(type: "INTEGER", nullable: false),
                    source_ip_prefix = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    user_agent_hash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    csrf_secret_hash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    revoked_at = table.Column<long>(type: "INTEGER", nullable: true),
                    revoke_reason = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_session", x => x.id_hash);
                    table.CheckConstraint("ck_staff_session_expiry", "idle_expires_at <= absolute_expires_at");
                    table.ForeignKey(
                        name: "FK_staff_session_staff_user_staff_user_id",
                        column: x => x.staff_user_id,
                        principalTable: "staff_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "staff_user_role",
                columns: table => new
                {
                    staff_user_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    role_name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    granted_by_staff_user_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    granted_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_user_role", x => new { x.staff_user_id, x.role_name });
                    table.ForeignKey(
                        name: "FK_staff_user_role_role_role_name",
                        column: x => x.role_name,
                        principalTable: "role",
                        principalColumn: "name",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_staff_user_role_staff_user_staff_user_id",
                        column: x => x.staff_user_id,
                        principalTable: "staff_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "student_alias",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    student_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    alias_type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    display_value = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    normalized_value = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    recognition_enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_by_staff_user_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_student_alias", x => x.id);
                    table.CheckConstraint("ck_student_alias_type", "alias_type IN ('kanji','kana','romanized','old_name','spacing','handwriting_hint','other')");
                    table.ForeignKey(
                        name: "FK_student_alias_student_student_id",
                        column: x => x.student_id,
                        principalTable: "student",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "accepted_answer",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    question_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    answer_text = table.Column<string>(type: "TEXT", nullable: false),
                    normalized_text = table.Column<string>(type: "TEXT", nullable: false),
                    variant_type = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    case_policy = table.Column<string>(type: "TEXT", nullable: true),
                    width_policy = table.Column<string>(type: "TEXT", nullable: true),
                    punctuation_policy = table.Column<string>(type: "TEXT", nullable: true),
                    teacher_verified = table.Column<bool>(type: "INTEGER", nullable: false),
                    answer_provenance = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    source_file_reference_id = table.Column<string>(type: "TEXT", nullable: true),
                    source_page_number = table.Column<int>(type: "INTEGER", nullable: true),
                    source_region_id = table.Column<string>(type: "TEXT", nullable: true),
                    locale = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_accepted_answer", x => x.id);
                    table.CheckConstraint("ck_accepted_answer_provenance", "answer_provenance IN ('provided_model_answer','teacher_entered','ai_proposed','derived_variant')");
                    table.CheckConstraint("ck_accepted_answer_variant", "variant_type IN ('canonical','equivalent','phonetic_exception','numeric','regex_restricted','choice')");
                });

            migrationBuilder.CreateTable(
                name: "grading_run",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    submission_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    run_number = table.Column<int>(type: "INTEGER", nullable: false),
                    template_version_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    reason = table.Column<string>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    provider = table.Column<string>(type: "TEXT", nullable: true),
                    model = table.Column<string>(type: "TEXT", nullable: true),
                    prompt_version = table.Column<string>(type: "TEXT", nullable: true),
                    schema_version = table.Column<string>(type: "TEXT", nullable: true),
                    pipeline_version = table.Column<string>(type: "TEXT", nullable: false),
                    canonical_input_manifest_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    earned_points_milli = table.Column<long>(type: "INTEGER", nullable: false),
                    possible_points_milli = table.Column<long>(type: "INTEGER", nullable: false),
                    result_source_revision = table.Column<long>(type: "INTEGER", nullable: false),
                    ai_usage_aggregation_json = table.Column<string>(type: "TEXT", nullable: true),
                    supersedes_grading_run_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    finished_at = table.Column<long>(type: "INTEGER", nullable: true),
                    activated_at = table.Column<long>(type: "INTEGER", nullable: true),
                    activated_by_staff_user_id = table.Column<string>(type: "TEXT", nullable: true),
                    finalized_at = table.Column<long>(type: "INTEGER", nullable: true),
                    finalized_by_staff_user_id = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_grading_run", x => x.id);
                    table.CheckConstraint("ck_grading_run_number", "run_number > 0");
                    table.CheckConstraint("ck_grading_run_points", "earned_points_milli >= 0 AND possible_points_milli >= 0 AND earned_points_milli <= possible_points_milli");
                    table.ForeignKey(
                        name: "FK_grading_run_grading_run_supersedes_grading_run_id",
                        column: x => x.supersedes_grading_run_id,
                        principalTable: "grading_run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "question",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    template_version_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    logical_question_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    order_index = table.Column<int>(type: "INTEGER", nullable: false),
                    display_label = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    question_text = table.Column<string>(type: "TEXT", nullable: false),
                    question_type = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    grading_mode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    max_points_milli = table.Column<long>(type: "INTEGER", nullable: false),
                    allow_non_kanji = table.Column<bool>(type: "INTEGER", nullable: false),
                    kanji_policy_note = table.Column<string>(type: "TEXT", nullable: true),
                    question_region_id = table.Column<string>(type: "TEXT", nullable: true),
                    answer_region_id = table.Column<string>(type: "TEXT", nullable: true),
                    requires_review_always = table.Column<bool>(type: "INTEGER", nullable: false),
                    ai_confidence_basis_points = table.Column<int>(type: "INTEGER", nullable: true),
                    teacher_verified = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_question", x => x.id);
                    table.CheckConstraint("ck_question_confidence", "ai_confidence_basis_points IS NULL OR ai_confidence_basis_points BETWEEN 0 AND 10000");
                    table.CheckConstraint("ck_question_grading_mode", "grading_mode IN ('deterministic','transcribe_then_rules','ai_rubric','manual')");
                    table.CheckConstraint("ck_question_points", "max_points_milli >= 0");
                    table.CheckConstraint("ck_question_type", "question_type IN ('multiple_choice','boolean','numeric','exact_short_text','semantic_short_text','multi_part','subjective','unsupported')");
                });

            migrationBuilder.CreateTable(
                name: "question_result",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    grading_run_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    question_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    transcribed_answer = table.Column<string>(type: "TEXT", nullable: true),
                    normalized_answer = table.Column<string>(type: "TEXT", nullable: true),
                    proposed_points_milli = table.Column<long>(type: "INTEGER", nullable: false),
                    maximum_points_milli = table.Column<long>(type: "INTEGER", nullable: false),
                    outcome = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    method = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    confidence_basis_points = table.Column<int>(type: "INTEGER", nullable: false),
                    kanji_check = table.Column<string>(type: "TEXT", nullable: false),
                    reason_code = table.Column<string>(type: "TEXT", nullable: true),
                    explanation = table.Column<string>(type: "TEXT", nullable: true),
                    answer_crop_file_reference_id = table.Column<string>(type: "TEXT", nullable: true),
                    review_required = table.Column<bool>(type: "INTEGER", nullable: false),
                    review_status = table.Column<string>(type: "TEXT", nullable: false),
                    model_response_item_hash = table.Column<string>(type: "TEXT", nullable: true),
                    current_revision_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_question_result", x => x.id);
                    table.CheckConstraint("ck_question_result_confidence", "confidence_basis_points BETWEEN 0 AND 10000");
                    table.CheckConstraint("ck_question_result_points", "proposed_points_milli >= 0 AND maximum_points_milli >= 0 AND proposed_points_milli <= maximum_points_milli");
                    table.ForeignKey(
                        name: "FK_question_result_grading_run_grading_run_id",
                        column: x => x.grading_run_id,
                        principalTable: "grading_run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_question_result_question_question_id",
                        column: x => x.question_id,
                        principalTable: "question",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "result_revision",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    question_result_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    revision_number = table.Column<int>(type: "INTEGER", nullable: false),
                    awarded_points_milli = table.Column<long>(type: "INTEGER", nullable: false),
                    outcome = table.Column<string>(type: "TEXT", nullable: false),
                    answer_text_correction = table.Column<string>(type: "TEXT", nullable: true),
                    reason_code = table.Column<string>(type: "TEXT", nullable: true),
                    teacher_note = table.Column<string>(type: "TEXT", nullable: true),
                    source = table.Column<string>(type: "TEXT", nullable: false),
                    actor_staff_user_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    supersedes_revision_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_result_revision", x => x.id);
                    table.CheckConstraint("ck_result_revision_number", "revision_number > 0");
                    table.CheckConstraint("ck_result_revision_points", "awarded_points_milli >= 0");
                    table.CheckConstraint("ck_result_revision_source", "source IN ('initial','teacher_override','regrade_adoption','system_correction')");
                    table.ForeignKey(
                        name: "FK_result_revision_question_result_question_result_id",
                        column: x => x.question_result_id,
                        principalTable: "question_result",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_result_revision_result_revision_supersedes_revision_id",
                        column: x => x.supersedes_revision_id,
                        principalTable: "result_revision",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "session_roster_member",
                columns: table => new
                {
                    test_session_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    student_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    expected = table.Column<bool>(type: "INTEGER", nullable: false),
                    seat_label = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_roster_member", x => new { x.test_session_id, x.student_id });
                    table.ForeignKey(
                        name: "FK_session_roster_member_student_student_id",
                        column: x => x.student_id,
                        principalTable: "student",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "submission",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    test_session_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    scan_payload_state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    assigned_student_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                    assignment_method = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    assignment_confidence_basis_points = table.Column<int>(type: "INTEGER", nullable: true),
                    assignment_policy_version = table.Column<string>(type: "TEXT", nullable: true),
                    assignment_evidence_json = table.Column<string>(type: "TEXT", nullable: true),
                    attempt_number = table.Column<int>(type: "INTEGER", nullable: false),
                    canonical_for_session = table.Column<bool>(type: "INTEGER", nullable: false),
                    uploaded_by_staff_user_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    original_file_name = table.Column<string>(type: "TEXT", nullable: true),
                    original_file_object_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                    upload_completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    quality_summary_json = table.Column<string>(type: "TEXT", nullable: true),
                    current_grading_run_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                    finalized_by_staff_user_id = table.Column<string>(type: "TEXT", nullable: true),
                    finalized_at = table.Column<long>(type: "INTEGER", nullable: true),
                    voided_by_staff_user_id = table.Column<string>(type: "TEXT", nullable: true),
                    voided_at = table.Column<long>(type: "INTEGER", nullable: true),
                    void_reason = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_submission", x => x.id);
                    table.CheckConstraint("ck_submission_assignment_confidence", "assignment_confidence_basis_points IS NULL OR assignment_confidence_basis_points BETWEEN 0 AND 10000");
                    table.CheckConstraint("ck_submission_assignment_method", "assignment_method IN ('auto','teacher','student_number','none')");
                    table.CheckConstraint("ck_submission_attempt", "attempt_number > 0");
                    table.CheckConstraint("ck_submission_auto_assignment_evidence", "assignment_method <> 'auto' OR (assignment_policy_version IS NOT NULL AND assignment_evidence_json IS NOT NULL)");
                    table.CheckConstraint("ck_submission_scan_payload_state", "scan_payload_state IN ('scan_available','deletion_pending','scan_deleted')");
                    table.ForeignKey(
                        name: "FK_submission_file_object_original_file_object_id",
                        column: x => x.original_file_object_id,
                        principalTable: "file_object",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_submission_grading_run_current_grading_run_id",
                        column: x => x.current_grading_run_id,
                        principalTable: "grading_run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_submission_student_assigned_student_id",
                        column: x => x.assigned_student_id,
                        principalTable: "student",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "template_source",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    template_version_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    upload_session_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    file_reference_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                    source_role = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    display_name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    uploaded_by_staff_user_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_template_source", x => x.id);
                    table.CheckConstraint("ck_template_source_ordinal", "ordinal >= 0");
                    table.CheckConstraint("ck_template_source_role", "source_role IN ('blank_test','contains_model_answers','separate_answer_key')");
                });

            migrationBuilder.CreateTable(
                name: "template_version",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    test_template_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    version_number = table.Column<int>(type: "INTEGER", nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    based_on_version_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                    target_total_points_milli = table.Column<long>(type: "INTEGER", nullable: true),
                    default_allow_non_kanji = table.Column<bool>(type: "INTEGER", nullable: false),
                    pipeline_version = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ai_generation_provenance_id = table.Column<string>(type: "TEXT", nullable: true),
                    published_by_staff_user_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                    published_at = table.Column<long>(type: "INTEGER", nullable: true),
                    content_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_template_version", x => x.id);
                    table.CheckConstraint("ck_template_version_points", "target_total_points_milli IS NULL OR target_total_points_milli >= 0");
                    table.CheckConstraint("ck_template_version_published", "(state <> 'published') OR (published_at IS NOT NULL AND published_by_staff_user_id IS NOT NULL AND content_hash IS NOT NULL)");
                    table.CheckConstraint("ck_template_version_state", "state IN ('draft','generating','validating','published','superseded','retired')");
                    table.ForeignKey(
                        name: "FK_template_version_template_version_based_on_version_id",
                        column: x => x.based_on_version_id,
                        principalTable: "template_version",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "test_session",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    template_version_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    title_override = table.Column<string>(type: "TEXT", nullable: true),
                    test_date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    course = table.Column<string>(type: "TEXT", nullable: true),
                    class_label = table.Column<string>(type: "TEXT", nullable: true),
                    priority = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    expected_roster_enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_by_staff_user_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    closed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_session", x => x.id);
                    table.CheckConstraint("ck_test_session_priority", "priority IN ('economy','expedite')");
                    table.CheckConstraint("ck_test_session_state", "state IN ('draft','open','closed','archived')");
                    table.ForeignKey(
                        name: "FK_test_session_template_version_template_version_id",
                        column: x => x.template_version_id,
                        principalTable: "template_version",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "test_template",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    subject = table.Column<string>(type: "TEXT", nullable: true),
                    category = table.Column<string>(type: "TEXT", nullable: true),
                    course = table.Column<string>(type: "TEXT", nullable: true),
                    grade_label = table.Column<string>(type: "TEXT", nullable: true),
                    source = table.Column<string>(type: "TEXT", nullable: true),
                    notes = table.Column<string>(type: "TEXT", nullable: true),
                    state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    active_version_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                    created_by_staff_user_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_template", x => x.id);
                    table.CheckConstraint("ck_test_template_state", "state IN ('draft','active','retired','archived')");
                    table.ForeignKey(
                        name: "FK_test_template_template_version_active_version_id",
                        column: x => x.active_version_id,
                        principalTable: "template_version",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "upload_session",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    created_by_staff_user_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    purpose = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    test_session_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                    destination_type = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    destination_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    original_file_name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    declared_mime_type = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    expected_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    current_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    expected_sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    final_sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    incremental_hash_checkpoint_json = table.Column<string>(type: "TEXT", nullable: true),
                    incoming_relative_path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    expires_at = table.Column<long>(type: "INTEGER", nullable: false),
                    source_ip_prefix = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    idempotency_key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_upload_session", x => x.id);
                    table.CheckConstraint("ck_upload_session_bytes", "expected_bytes >= 0 AND current_bytes >= 0 AND current_bytes <= expected_bytes");
                    table.CheckConstraint("ck_upload_session_destination", "(purpose = 'completed_test' AND test_session_id IS NOT NULL) OR (purpose <> 'completed_test')");
                    table.CheckConstraint("ck_upload_session_state", "state IN ('uploading','finalizing','completed','cancelled','expired','failed')");
                    table.ForeignKey(
                        name: "FK_upload_session_test_session_test_session_id",
                        column: x => x.test_session_id,
                        principalTable: "test_session",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "role",
                columns: new[] { "name", "display_name" },
                values: new object[,]
                {
                    { "administrator", "Administrator" },
                    { "readOnlyReviewer", "Read-only reviewer" },
                    { "scanOperator", "Scan operator" },
                    { "teacher", "Teacher" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_accepted_answer_question_id_normalized_text_variant_type",
                table: "accepted_answer",
                columns: new[] { "question_id", "normalized_text", "variant_type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_audit_event_actor_staff_user_id",
                table: "audit_event",
                column: "actor_staff_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_audit_event_object_type_object_id",
                table: "audit_event",
                columns: new[] { "object_type", "object_id" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_event_occurred_at",
                table: "audit_event",
                column: "occurred_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_background_job_deduplication_key",
                table: "background_job",
                column: "deduplication_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_background_job_state_priority_next_attempt_at_created_at",
                table: "background_job",
                columns: new[] { "state", "priority", "next_attempt_at", "created_at" },
                descending: new[] { false, true, false, false });

            migrationBuilder.CreateIndex(
                name: "IX_file_object_state",
                table: "file_object",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "IX_file_object_storage_class_sha256",
                table: "file_object",
                columns: new[] { "storage_class", "sha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_file_reference_file_object_id",
                table: "file_reference",
                column: "file_object_id");

            migrationBuilder.CreateIndex(
                name: "IX_file_reference_owner_type_owner_id",
                table: "file_reference",
                columns: new[] { "owner_type", "owner_id" });

            migrationBuilder.CreateIndex(
                name: "IX_file_reference_retention_anchor_at",
                table: "file_reference",
                column: "retention_anchor_at");

            migrationBuilder.CreateIndex(
                name: "IX_grading_run_state",
                table: "grading_run",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "IX_grading_run_submission_id_run_number",
                table: "grading_run",
                columns: new[] { "submission_id", "run_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_grading_run_supersedes_grading_run_id",
                table: "grading_run",
                column: "supersedes_grading_run_id");

            migrationBuilder.CreateIndex(
                name: "IX_grading_run_template_version_id",
                table: "grading_run",
                column: "template_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_idempotency_record_actor_key_route_idempotency_key",
                table: "idempotency_record",
                columns: new[] { "actor_key", "route", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_idempotency_record_expires_at",
                table: "idempotency_record",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_event_delivered_at_occurred_at_id",
                table: "outbox_event",
                columns: new[] { "delivered_at", "occurred_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_question_template_version_id_display_label",
                table: "question",
                columns: new[] { "template_version_id", "display_label" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_question_template_version_id_order_index",
                table: "question",
                columns: new[] { "template_version_id", "order_index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_question_result_current_revision_id",
                table: "question_result",
                column: "current_revision_id");

            migrationBuilder.CreateIndex(
                name: "IX_question_result_grading_run_id_question_id",
                table: "question_result",
                columns: new[] { "grading_run_id", "question_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_question_result_question_id",
                table: "question_result",
                column: "question_id");

            migrationBuilder.CreateIndex(
                name: "IX_result_revision_question_result_id_revision_number",
                table: "result_revision",
                columns: new[] { "question_result_id", "revision_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_result_revision_supersedes_revision_id",
                table: "result_revision",
                column: "supersedes_revision_id");

            migrationBuilder.CreateIndex(
                name: "IX_session_roster_member_student_id",
                table: "session_roster_member",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "IX_staff_session_staff_user_id_idle_expires_at",
                table: "staff_session",
                columns: new[] { "staff_user_id", "idle_expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_staff_user_username_normalized",
                table: "staff_user",
                column: "username_normalized",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_staff_user_role_role_name",
                table: "staff_user_role",
                column: "role_name");

            migrationBuilder.CreateIndex(
                name: "IX_student_family_name_kana_normalized_given_name_kana_normalized",
                table: "student",
                columns: new[] { "family_name_kana_normalized", "given_name_kana_normalized" });

            migrationBuilder.CreateIndex(
                name: "IX_student_family_name_normalized_given_name_normalized",
                table: "student",
                columns: new[] { "family_name_normalized", "given_name_normalized" });

            migrationBuilder.CreateIndex(
                name: "IX_student_merged_into_student_id",
                table: "student",
                column: "merged_into_student_id");

            migrationBuilder.CreateIndex(
                name: "IX_student_student_number_normalized",
                table: "student",
                column: "student_number_normalized",
                unique: true,
                filter: "\"status\" <> 'merged'");

            migrationBuilder.CreateIndex(
                name: "IX_student_alias_normalized_value",
                table: "student_alias",
                column: "normalized_value");

            migrationBuilder.CreateIndex(
                name: "IX_student_alias_student_id_normalized_value_alias_type",
                table: "student_alias",
                columns: new[] { "student_id", "normalized_value", "alias_type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_submission_assigned_student_id",
                table: "submission",
                column: "assigned_student_id");

            migrationBuilder.CreateIndex(
                name: "IX_submission_current_grading_run_id",
                table: "submission",
                column: "current_grading_run_id");

            migrationBuilder.CreateIndex(
                name: "IX_submission_original_file_object_id",
                table: "submission",
                column: "original_file_object_id");

            migrationBuilder.CreateIndex(
                name: "IX_submission_scan_payload_state",
                table: "submission",
                column: "scan_payload_state");

            migrationBuilder.CreateIndex(
                name: "IX_submission_test_session_id_assigned_student_id",
                table: "submission",
                columns: new[] { "test_session_id", "assigned_student_id" },
                unique: true,
                filter: "\"assigned_student_id\" IS NOT NULL AND \"canonical_for_session\" = 1 AND \"voided_at\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_submission_test_session_id_assigned_student_id_state",
                table: "submission",
                columns: new[] { "test_session_id", "assigned_student_id", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_submission_upload_completed_at",
                table: "submission",
                column: "upload_completed_at");

            migrationBuilder.CreateIndex(
                name: "IX_template_source_template_version_id_ordinal",
                table: "template_source",
                columns: new[] { "template_version_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_template_source_upload_session_id",
                table: "template_source",
                column: "upload_session_id");

            migrationBuilder.CreateIndex(
                name: "IX_template_version_based_on_version_id",
                table: "template_version",
                column: "based_on_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_template_version_test_template_id_version_number",
                table: "template_version",
                columns: new[] { "test_template_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_test_session_template_version_id",
                table: "test_session",
                column: "template_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_test_session_test_date_state",
                table: "test_session",
                columns: new[] { "test_date", "state" },
                descending: new[] { true, false });

            migrationBuilder.CreateIndex(
                name: "IX_test_template_active_version_id",
                table: "test_template",
                column: "active_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_test_template_state_title",
                table: "test_template",
                columns: new[] { "state", "title" });

            migrationBuilder.CreateIndex(
                name: "IX_upload_session_created_by_staff_user_id_idempotency_key",
                table: "upload_session",
                columns: new[] { "created_by_staff_user_id", "idempotency_key" },
                unique: true,
                filter: "\"idempotency_key\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_upload_session_expires_at",
                table: "upload_session",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_upload_session_test_session_id",
                table: "upload_session",
                column: "test_session_id");

            migrationBuilder.AddForeignKey(
                name: "FK_accepted_answer_question_question_id",
                table: "accepted_answer",
                column: "question_id",
                principalTable: "question",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_grading_run_submission_submission_id",
                table: "grading_run",
                column: "submission_id",
                principalTable: "submission",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_grading_run_template_version_template_version_id",
                table: "grading_run",
                column: "template_version_id",
                principalTable: "template_version",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_question_template_version_template_version_id",
                table: "question",
                column: "template_version_id",
                principalTable: "template_version",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_question_result_result_revision_current_revision_id",
                table: "question_result",
                column: "current_revision_id",
                principalTable: "result_revision",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_session_roster_member_test_session_test_session_id",
                table: "session_roster_member",
                column: "test_session_id",
                principalTable: "test_session",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_submission_test_session_test_session_id",
                table: "submission",
                column: "test_session_id",
                principalTable: "test_session",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_template_source_template_version_template_version_id",
                table: "template_source",
                column: "template_version_id",
                principalTable: "template_version",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_template_source_upload_session_upload_session_id",
                table: "template_source",
                column: "upload_session_id",
                principalTable: "upload_session",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_template_version_test_template_test_template_id",
                table: "template_version",
                column: "test_template_id",
                principalTable: "test_template",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_question_result_question_question_id",
                table: "question_result");

            migrationBuilder.DropForeignKey(
                name: "FK_submission_file_object_original_file_object_id",
                table: "submission");

            migrationBuilder.DropForeignKey(
                name: "FK_grading_run_submission_submission_id",
                table: "grading_run");

            migrationBuilder.DropForeignKey(
                name: "FK_grading_run_template_version_template_version_id",
                table: "grading_run");

            migrationBuilder.DropForeignKey(
                name: "FK_test_template_template_version_active_version_id",
                table: "test_template");

            migrationBuilder.DropForeignKey(
                name: "FK_question_result_grading_run_grading_run_id",
                table: "question_result");

            migrationBuilder.DropForeignKey(
                name: "FK_question_result_result_revision_current_revision_id",
                table: "question_result");

            migrationBuilder.DropTable(
                name: "accepted_answer");

            migrationBuilder.DropTable(
                name: "audit_event");

            migrationBuilder.DropTable(
                name: "background_job");

            migrationBuilder.DropTable(
                name: "file_reference");

            migrationBuilder.DropTable(
                name: "idempotency_record");

            migrationBuilder.DropTable(
                name: "outbox_event");

            migrationBuilder.DropTable(
                name: "session_roster_member");

            migrationBuilder.DropTable(
                name: "site_settings");

            migrationBuilder.DropTable(
                name: "staff_session");

            migrationBuilder.DropTable(
                name: "staff_user_role");

            migrationBuilder.DropTable(
                name: "student_alias");

            migrationBuilder.DropTable(
                name: "template_source");

            migrationBuilder.DropTable(
                name: "role");

            migrationBuilder.DropTable(
                name: "staff_user");

            migrationBuilder.DropTable(
                name: "upload_session");

            migrationBuilder.DropTable(
                name: "question");

            migrationBuilder.DropTable(
                name: "file_object");

            migrationBuilder.DropTable(
                name: "submission");

            migrationBuilder.DropTable(
                name: "student");

            migrationBuilder.DropTable(
                name: "test_session");

            migrationBuilder.DropTable(
                name: "template_version");

            migrationBuilder.DropTable(
                name: "test_template");

            migrationBuilder.DropTable(
                name: "grading_run");

            migrationBuilder.DropTable(
                name: "result_revision");

            migrationBuilder.DropTable(
                name: "question_result");
        }
    }
}
