using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OokiGrader.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0010_LocalProcessingBackupsReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_question_points",
                table: "question");

            migrationBuilder.AddColumn<int>(
                name: "page_count",
                table: "submission",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "preprocessing_completed_at",
                table: "submission",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "preprocessing_manifest_hash",
                table: "submission",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "preprocessing_pipeline_version",
                table: "submission",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "point_increment_milli",
                table: "question",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.CreateTable(
                name: "backup_policy",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    destination_root_path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    destination_encryption_confirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    include_managed_scans = table.Column<bool>(type: "INTEGER", nullable: false),
                    include_reports = table.Column<bool>(type: "INTEGER", nullable: false),
                    schedule_local_hour = table.Column<int>(type: "INTEGER", nullable: false),
                    schedule_local_minute = table.Column<int>(type: "INTEGER", nullable: false),
                    daily_retention_days = table.Column<int>(type: "INTEGER", nullable: false),
                    weekly_retention_weeks = table.Column<int>(type: "INTEGER", nullable: false),
                    monthly_retention_months = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_policy", x => x.id);
                    table.CheckConstraint("ck_backup_policy_destination", "enabled = 0 OR destination_root_path IS NOT NULL");
                    table.CheckConstraint("ck_backup_policy_retention", "daily_retention_days > 0 AND weekly_retention_weeks > 0 AND monthly_retention_months > 0");
                    table.CheckConstraint("ck_backup_policy_scan_encryption", "include_managed_scans = 0 OR destination_encryption_confirmed = 1");
                    table.CheckConstraint("ck_backup_policy_schedule", "schedule_local_hour BETWEEN 0 AND 23 AND schedule_local_minute BETWEEN 0 AND 59");
                });

            migrationBuilder.CreateTable(
                name: "export_record",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    submission_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    grading_run_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    result_source_revision = table.Column<long>(type: "INTEGER", nullable: false),
                    submission_revision_at_create = table.Column<long>(type: "INTEGER", nullable: false),
                    template_version_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    template_version_number = table.Column<int>(type: "INTEGER", nullable: false),
                    export_revision = table.Column<int>(type: "INTEGER", nullable: false),
                    type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    renderer_version = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    source_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    background_job_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                    file_reference_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                    sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    bytes = table.Column<long>(type: "INTEGER", nullable: true),
                    page_count = table.Column<int>(type: "INTEGER", nullable: true),
                    state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    error_code = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    safe_error_detail = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    created_by_staff_user_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    started_at = table.Column<long>(type: "INTEGER", nullable: true),
                    completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    superseded_at = table.Column<long>(type: "INTEGER", nullable: true),
                    superseded_reason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_export_record", x => x.id);
                    table.CheckConstraint("ck_export_record_revisions", "result_source_revision > 0 AND submission_revision_at_create > 0 AND template_version_number > 0 AND export_revision > 0");
                    table.CheckConstraint("ck_export_record_sizes", "(bytes IS NULL OR bytes >= 0) AND (page_count IS NULL OR page_count > 0)");
                    table.CheckConstraint("ck_export_record_state", "state IN ('queued','rendering','verified','failed','superseded')");
                    table.CheckConstraint("ck_export_record_superseded", "superseded_at IS NULL OR superseded_reason IS NOT NULL");
                    table.CheckConstraint("ck_export_record_type", "type = 'result_pdf'");
                    table.CheckConstraint("ck_export_record_verified", "state <> 'verified' OR (file_reference_id IS NOT NULL AND sha256 IS NOT NULL AND bytes IS NOT NULL AND page_count IS NOT NULL AND completed_at IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_export_record_background_job_background_job_id",
                        column: x => x.background_job_id,
                        principalTable: "background_job",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_export_record_file_reference_file_reference_id",
                        column: x => x.file_reference_id,
                        principalTable: "file_reference",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_export_record_grading_run_grading_run_id",
                        column: x => x.grading_run_id,
                        principalTable: "grading_run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_export_record_staff_user_created_by_staff_user_id",
                        column: x => x.created_by_staff_user_id,
                        principalTable: "staff_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_export_record_submission_submission_id",
                        column: x => x.submission_id,
                        principalTable: "submission",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_export_record_template_version_template_version_id",
                        column: x => x.template_version_id,
                        principalTable: "template_version",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "submission_page",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    submission_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    page_number = table.Column<int>(type: "INTEGER", nullable: false),
                    normalized_file_reference_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    thumbnail_file_reference_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    width_pixels = table.Column<int>(type: "INTEGER", nullable: false),
                    height_pixels = table.Column<int>(type: "INTEGER", nullable: false),
                    rotation_degrees = table.Column<int>(type: "INTEGER", nullable: false),
                    source_sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    normalized_sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    difference_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    perceptual_hash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    quality_state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    blur_basis_points = table.Column<int>(type: "INTEGER", nullable: false),
                    contrast_basis_points = table.Column<int>(type: "INTEGER", nullable: false),
                    brightness_basis_points = table.Column<int>(type: "INTEGER", nullable: false),
                    ink_coverage_basis_points = table.Column<int>(type: "INTEGER", nullable: false),
                    alignment_state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    alignment_score_basis_points = table.Column<int>(type: "INTEGER", nullable: true),
                    repeated_page_number = table.Column<int>(type: "INTEGER", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_submission_page", x => x.id);
                    table.CheckConstraint("ck_submission_page_alignment_state", "alignment_state IN ('not_configured','aligned','warning','failed')");
                    table.CheckConstraint("ck_submission_page_dimensions", "width_pixels > 0 AND height_pixels > 0");
                    table.CheckConstraint("ck_submission_page_number", "page_number > 0");
                    table.CheckConstraint("ck_submission_page_quality_metrics", "blur_basis_points BETWEEN 0 AND 10000 AND contrast_basis_points BETWEEN 0 AND 10000 AND brightness_basis_points BETWEEN 0 AND 10000 AND ink_coverage_basis_points BETWEEN 0 AND 10000 AND (alignment_score_basis_points IS NULL OR alignment_score_basis_points BETWEEN 0 AND 10000)");
                    table.CheckConstraint("ck_submission_page_quality_state", "quality_state IN ('accepted','warning','rejected')");
                    table.CheckConstraint("ck_submission_page_repeat", "repeated_page_number IS NULL OR (repeated_page_number > 0 AND repeated_page_number <> page_number)");
                    table.CheckConstraint("ck_submission_page_rotation", "rotation_degrees IN (0,90,180,270)");
                    table.ForeignKey(
                        name: "FK_submission_page_file_reference_normalized_file_reference_id",
                        column: x => x.normalized_file_reference_id,
                        principalTable: "file_reference",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_submission_page_file_reference_thumbnail_file_reference_id",
                        column: x => x.thumbnail_file_reference_id,
                        principalTable: "file_reference",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_submission_page_submission_submission_id",
                        column: x => x.submission_id,
                        principalTable: "submission",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "visual_duplicate",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    submission_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    candidate_submission_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    hamming_distance = table.Column<int>(type: "INTEGER", nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    resolved_at = table.Column<long>(type: "INTEGER", nullable: true),
                    resolved_by_staff_user_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_visual_duplicate", x => x.id);
                    table.CheckConstraint("ck_visual_duplicate_distance", "hamming_distance BETWEEN 0 AND 64");
                    table.CheckConstraint("ck_visual_duplicate_order", "submission_id < candidate_submission_id");
                    table.CheckConstraint("ck_visual_duplicate_resolution", "(state = 'possible' AND resolved_at IS NULL) OR (state <> 'possible' AND resolved_at IS NOT NULL)");
                    table.CheckConstraint("ck_visual_duplicate_state", "state IN ('possible','confirmed','dismissed')");
                    table.ForeignKey(
                        name: "FK_visual_duplicate_submission_candidate_submission_id",
                        column: x => x.candidate_submission_id,
                        principalTable: "submission",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_visual_duplicate_submission_submission_id",
                        column: x => x.submission_id,
                        principalTable: "submission",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "backup_record",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    backup_policy_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    background_job_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    trigger = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    created_by_staff_user_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                    destination_relative_path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    manifest_sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    database_sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    database_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    object_count = table.Column<int>(type: "INTEGER", nullable: false),
                    object_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    secret_envelope_count = table.Column<int>(type: "INTEGER", nullable: false),
                    secret_envelope_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    database_migration_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    database_data_version = table.Column<long>(type: "INTEGER", nullable: false),
                    application_version = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    integrity_result = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    last_verification_at = table.Column<long>(type: "INTEGER", nullable: true),
                    error_code = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    safe_error_detail = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    requested_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    started_at = table.Column<long>(type: "INTEGER", nullable: true),
                    completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    verified_at = table.Column<long>(type: "INTEGER", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_record", x => x.id);
                    table.CheckConstraint("ck_backup_record_sizes", "database_bytes >= 0 AND object_count >= 0 AND object_bytes >= 0 AND secret_envelope_count >= 0 AND secret_envelope_bytes >= 0 AND database_data_version >= 0");
                    table.CheckConstraint("ck_backup_record_state", "state IN ('queued','running','verifying','verified','failed','expired')");
                    table.CheckConstraint("ck_backup_record_trigger", "trigger IN ('manual','scheduled','pre_upgrade')");
                    table.ForeignKey(
                        name: "FK_backup_record_background_job_background_job_id",
                        column: x => x.background_job_id,
                        principalTable: "background_job",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_backup_record_backup_policy_backup_policy_id",
                        column: x => x.backup_policy_id,
                        principalTable: "backup_policy",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "submission_artifact",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    submission_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    submission_page_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    question_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                    region_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                    file_reference_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    artifact_type = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    panel_label = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    input_manifest_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    width_pixels = table.Column<int>(type: "INTEGER", nullable: false),
                    height_pixels = table.Column<int>(type: "INTEGER", nullable: false),
                    provider_disclosure_allowed = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_submission_artifact", x => x.id);
                    table.CheckConstraint("ck_submission_artifact_dimensions", "width_pixels > 0 AND height_pixels > 0");
                    table.CheckConstraint("ck_submission_artifact_minimization", "provider_disclosure_allowed = 0 OR (artifact_type = 'answer_crop' AND question_id IS NOT NULL) OR (artifact_type IN ('name_crop','student_number_crop') AND question_id IS NULL)");
                    table.CheckConstraint("ck_submission_artifact_ordinal", "ordinal >= 0");
                    table.CheckConstraint("ck_submission_artifact_type", "artifact_type IN ('answer_crop','name_crop','student_number_crop','alignment_diagnostic')");
                    table.ForeignKey(
                        name: "FK_submission_artifact_file_reference_file_reference_id",
                        column: x => x.file_reference_id,
                        principalTable: "file_reference",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_submission_artifact_question_question_id",
                        column: x => x.question_id,
                        principalTable: "question",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_submission_artifact_region_region_id",
                        column: x => x.region_id,
                        principalTable: "region",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_submission_artifact_submission_page_submission_page_id",
                        column: x => x.submission_page_id,
                        principalTable: "submission_page",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_submission_artifact_submission_submission_id",
                        column: x => x.submission_id,
                        principalTable: "submission",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_submission_preprocessing_manifest_hash",
                table: "submission",
                column: "preprocessing_manifest_hash");

            migrationBuilder.AddCheckConstraint(
                name: "ck_submission_page_count",
                table: "submission",
                sql: "page_count IS NULL OR page_count > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_submission_preprocessing_completion",
                table: "submission",
                sql: "preprocessing_completed_at IS NULL OR (preprocessing_pipeline_version IS NOT NULL AND preprocessing_manifest_hash IS NOT NULL AND page_count IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_question_points",
                table: "question",
                sql: "max_points_milli > 0 AND point_increment_milli > 0 AND point_increment_milli <= max_points_milli AND max_points_milli % point_increment_milli = 0");

            migrationBuilder.CreateIndex(
                name: "IX_backup_policy_enabled",
                table: "backup_policy",
                column: "enabled");

            migrationBuilder.CreateIndex(
                name: "IX_backup_record_background_job_id",
                table: "backup_record",
                column: "background_job_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_backup_record_backup_policy_id",
                table: "backup_record",
                column: "backup_policy_id");

            migrationBuilder.CreateIndex(
                name: "IX_backup_record_completed_at",
                table: "backup_record",
                column: "completed_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_backup_record_state_requested_at_id",
                table: "backup_record",
                columns: new[] { "state", "requested_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_export_record_background_job_id",
                table: "export_record",
                column: "background_job_id",
                unique: true,
                filter: "\"background_job_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_export_record_created_by_staff_user_id",
                table: "export_record",
                column: "created_by_staff_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_export_record_file_reference_id",
                table: "export_record",
                column: "file_reference_id",
                unique: true,
                filter: "\"file_reference_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_export_record_grading_run_id",
                table: "export_record",
                column: "grading_run_id");

            migrationBuilder.CreateIndex(
                name: "IX_export_record_state_created_at_id",
                table: "export_record",
                columns: new[] { "state", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_export_record_submission_id_created_at_id",
                table: "export_record",
                columns: new[] { "submission_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_export_record_submission_id_export_revision",
                table: "export_record",
                columns: new[] { "submission_id", "export_revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_export_record_template_version_id",
                table: "export_record",
                column: "template_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_submission_artifact_file_reference_id",
                table: "submission_artifact",
                column: "file_reference_id");

            migrationBuilder.CreateIndex(
                name: "IX_submission_artifact_question_id",
                table: "submission_artifact",
                column: "question_id");

            migrationBuilder.CreateIndex(
                name: "IX_submission_artifact_region_id",
                table: "submission_artifact",
                column: "region_id");

            migrationBuilder.CreateIndex(
                name: "IX_submission_artifact_submission_id_artifact_type_question_id_ordinal",
                table: "submission_artifact",
                columns: new[] { "submission_id", "artifact_type", "question_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_submission_artifact_submission_id_provider_disclosure_allowed",
                table: "submission_artifact",
                columns: new[] { "submission_id", "provider_disclosure_allowed" });

            migrationBuilder.CreateIndex(
                name: "IX_submission_artifact_submission_page_id",
                table: "submission_artifact",
                column: "submission_page_id");

            migrationBuilder.CreateIndex(
                name: "IX_submission_page_difference_hash",
                table: "submission_page",
                column: "difference_hash");

            migrationBuilder.CreateIndex(
                name: "IX_submission_page_normalized_file_reference_id",
                table: "submission_page",
                column: "normalized_file_reference_id");

            migrationBuilder.CreateIndex(
                name: "IX_submission_page_submission_id_page_number",
                table: "submission_page",
                columns: new[] { "submission_id", "page_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_submission_page_thumbnail_file_reference_id",
                table: "submission_page",
                column: "thumbnail_file_reference_id");

            migrationBuilder.CreateIndex(
                name: "IX_visual_duplicate_candidate_submission_id",
                table: "visual_duplicate",
                column: "candidate_submission_id");

            migrationBuilder.CreateIndex(
                name: "IX_visual_duplicate_state_created_at_id",
                table: "visual_duplicate",
                columns: new[] { "state", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_visual_duplicate_submission_id_candidate_submission_id",
                table: "visual_duplicate",
                columns: new[] { "submission_id", "candidate_submission_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "backup_record");

            migrationBuilder.DropTable(
                name: "export_record");

            migrationBuilder.DropTable(
                name: "submission_artifact");

            migrationBuilder.DropTable(
                name: "visual_duplicate");

            migrationBuilder.DropTable(
                name: "backup_policy");

            migrationBuilder.DropTable(
                name: "submission_page");

            migrationBuilder.DropIndex(
                name: "IX_submission_preprocessing_manifest_hash",
                table: "submission");

            migrationBuilder.DropCheckConstraint(
                name: "ck_submission_page_count",
                table: "submission");

            migrationBuilder.DropCheckConstraint(
                name: "ck_submission_preprocessing_completion",
                table: "submission");

            migrationBuilder.DropCheckConstraint(
                name: "ck_question_points",
                table: "question");

            migrationBuilder.DropColumn(
                name: "page_count",
                table: "submission");

            migrationBuilder.DropColumn(
                name: "preprocessing_completed_at",
                table: "submission");

            migrationBuilder.DropColumn(
                name: "preprocessing_manifest_hash",
                table: "submission");

            migrationBuilder.DropColumn(
                name: "preprocessing_pipeline_version",
                table: "submission");

            migrationBuilder.DropColumn(
                name: "point_increment_milli",
                table: "question");

            migrationBuilder.AddCheckConstraint(
                name: "ck_question_points",
                table: "question",
                sql: "max_points_milli >= 0");
        }
    }
}
