using Microsoft.EntityFrameworkCore.Migrations;
using OokiGrader.Infrastructure.Persistence;

#nullable disable

namespace OokiGrader.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0017_DeterministicTemplateGenerationBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Only the content-immutability trigger must be versioned for the
            // additive provenance columns below. Other live triggers remain in
            // place because this migration deliberately avoids rebuilding
            // template_version.
            migrationBuilder.Sql(
                TemplateVersionIntegrityTriggerCatalog
                    .DropPublishedVersionContentImmutableStatement);

            migrationBuilder.AddColumn<string>(
                name: "answer_style",
                table: "template_version",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "generation_profile_hash",
                table: "template_version",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "generation_profile_json",
                table: "template_version",
                type: "TEXT",
                maxLength: 64000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "generation_profile_version",
                table: "template_version",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "originating_batch_id",
                table: "template_version",
                type: "TEXT",
                fixedLength: true,
                maxLength: 26,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "originating_unit_id",
                table: "template_version",
                type: "TEXT",
                fixedLength: true,
                maxLength: 26,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "printed_test_name",
                table: "template_version",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "prompt_system",
                table: "template_version",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "resolved_grade",
                table: "template_version",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "step_set_index",
                table: "template_version",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "step_variation_index",
                table: "template_version",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "test_type",
                table: "template_version",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "template_generation_batch",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    test_type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    subject = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    answer_style = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    prompt_system = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    source_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    source_page_count = table.Column<int>(type: "INTEGER", nullable: false),
                    expected_unit_count = table.Column<int>(type: "INTEGER", nullable: false),
                    completed_unit_count = table.Column<int>(type: "INTEGER", nullable: false),
                    failed_unit_count = table.Column<int>(type: "INTEGER", nullable: false),
                    current_operation_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    plan_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    created_by_user_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    last_error_code = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_template_generation_batch", x => x.id);
                    table.CheckConstraint("ck_template_generation_batch_counts", "source_page_count > 0 AND expected_unit_count > 0 AND completed_unit_count >= 0 AND failed_unit_count >= 0 AND completed_unit_count <= expected_unit_count AND failed_unit_count <= expected_unit_count AND completed_unit_count + failed_unit_count <= expected_unit_count");
                    table.CheckConstraint("ck_template_generation_batch_route", "(test_type IN ('Hop','Step') AND answer_style IS NULL AND prompt_system = 'Standard') OR (test_type = 'ClassPlacement' AND answer_style IS NULL AND prompt_system = 'ClassPlacement') OR (test_type = 'Other' AND answer_style = 'Normal' AND prompt_system = 'Standard') OR (test_type = 'Other' AND answer_style = 'FillBlank' AND prompt_system = 'FillBlank')");
                    table.CheckConstraint("ck_template_generation_batch_status", "status IN ('Draft','Validating','Generating','NeedsFinalCheck','Confirming','Completed','Failed','Cancelled')");
                    table.ForeignKey(
                        name: "FK_template_generation_batch_staff_user_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "staff_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_template_generation_batch_upload_session_source_id",
                        column: x => x.source_id,
                        principalTable: "upload_session",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "template_generation_unit",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    batch_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    test_type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    answer_style = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    first_page = table.Column<int>(type: "INTEGER", nullable: false),
                    last_page = table.Column<int>(type: "INTEGER", nullable: false),
                    step_set_index = table.Column<int>(type: "INTEGER", nullable: true),
                    step_variation_index = table.Column<int>(type: "INTEGER", nullable: true),
                    deterministic_suffix = table.Column<string>(type: "TEXT", maxLength: 2, nullable: true),
                    prompt_system = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    generation_profile_json = table.Column<string>(type: "TEXT", maxLength: 64000, nullable: false),
                    generation_profile_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    orientation_attempt_count = table.Column<int>(type: "INTEGER", nullable: false),
                    applied_rotations_json = table.Column<string>(type: "TEXT", maxLength: 16000, nullable: false),
                    derived_source_object_key = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    derived_source_sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    extraction_draft_json = table.Column<string>(type: "TEXT", maxLength: 1000000, nullable: true),
                    extraction_draft_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    printed_test_name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    user_confirmed_base_name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    final_template_name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    filename_grade = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    paper_grade = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    resolved_grade = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    grade_evidence = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    grade_confirmed_by_user = table.Column<bool>(type: "INTEGER", nullable: false),
                    warnings_json = table.Column<string>(type: "TEXT", maxLength: 64000, nullable: false),
                    teacher_note = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    created_template_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                    created_template_version_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                    extraction_job_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_template_generation_unit", x => x.id);
                    table.CheckConstraint("ck_template_generation_unit_confirmed", "status <> 'Confirmed' OR (created_template_id IS NOT NULL AND created_template_version_id IS NOT NULL)");
                    table.CheckConstraint("ck_template_generation_unit_created_pair", "(created_template_id IS NULL AND created_template_version_id IS NULL) OR (created_template_id IS NOT NULL AND created_template_version_id IS NOT NULL)");
                    table.CheckConstraint("ck_template_generation_unit_hop_range", "test_type <> 'Hop' OR last_page = first_page");
                    table.CheckConstraint("ck_template_generation_unit_orientation_attempts", "orientation_attempt_count BETWEEN 0 AND 2");
                    table.CheckConstraint("ck_template_generation_unit_range", "sequence > 0 AND first_page >= 1 AND last_page >= first_page");
                    table.CheckConstraint("ck_template_generation_unit_route", "(test_type IN ('Hop','Step') AND answer_style IS NULL AND prompt_system = 'Standard') OR (test_type = 'ClassPlacement' AND answer_style IS NULL AND prompt_system = 'ClassPlacement') OR (test_type = 'Other' AND answer_style = 'Normal' AND prompt_system = 'Standard') OR (test_type = 'Other' AND answer_style = 'FillBlank' AND prompt_system = 'FillBlank')");
                    table.CheckConstraint("ck_template_generation_unit_status", "status IN ('Pending','Queued','Generating','Rotating','RetryingAfterRotation','Extracted','Failed','Confirmed')");
                    table.CheckConstraint("ck_template_generation_unit_step_metadata", "(test_type = 'Step' AND step_set_index > 0 AND step_variation_index BETWEEN 1 AND 3 AND deterministic_suffix = '-' || step_variation_index) OR (test_type <> 'Step' AND step_set_index IS NULL AND step_variation_index IS NULL AND deterministic_suffix IS NULL)");
                    table.CheckConstraint("ck_template_generation_unit_step_range", "test_type <> 'Step' OR last_page = first_page + 1");
                    table.ForeignKey(
                        name: "FK_template_generation_unit_background_job_extraction_job_id",
                        column: x => x.extraction_job_id,
                        principalTable: "background_job",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_template_generation_unit_template_generation_batch_batch_id",
                        column: x => x.batch_id,
                        principalTable: "template_generation_batch",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_template_generation_unit_template_version_created_template_version_id",
                        column: x => x.created_template_version_id,
                        principalTable: "template_version",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_template_generation_unit_test_template_created_template_id",
                        column: x => x.created_template_id,
                        principalTable: "test_template",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "template_generation_derived_source",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    unit_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    parent_source_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    parent_first_page = table.Column<int>(type: "INTEGER", nullable: false),
                    parent_last_page = table.Column<int>(type: "INTEGER", nullable: false),
                    original_content_sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    derivation_type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    applied_rotations_json = table.Column<string>(type: "TEXT", maxLength: 16000, nullable: false),
                    derivation_policy_version = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    derived_content_sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    file_reference_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_template_generation_derived_source", x => x.id);
                    table.CheckConstraint("ck_template_generation_derived_source_range", "parent_first_page >= 1 AND parent_last_page >= parent_first_page");
                    table.CheckConstraint("ck_template_generation_derived_source_type", "derivation_type IN ('pageRange','pageRangeAndRotation')");
                    table.ForeignKey(
                        name: "FK_template_generation_derived_source_file_reference_file_reference_id",
                        column: x => x.file_reference_id,
                        principalTable: "file_reference",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_template_generation_derived_source_template_generation_unit_unit_id",
                        column: x => x.unit_id,
                        principalTable: "template_generation_unit",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_template_generation_derived_source_upload_session_parent_source_id",
                        column: x => x.parent_source_id,
                        principalTable: "upload_session",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_template_version_originating_batch_id",
                table: "template_version",
                column: "originating_batch_id");

            migrationBuilder.CreateIndex(
                name: "IX_template_version_originating_unit_id",
                table: "template_version",
                column: "originating_unit_id");

            migrationBuilder.CreateIndex(
                name: "IX_template_generation_batch_created_by_user_id_status",
                table: "template_generation_batch",
                columns: new[] { "created_by_user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_template_generation_batch_source_id_created_at",
                table: "template_generation_batch",
                columns: new[] { "source_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_template_generation_derived_source_file_reference_id",
                table: "template_generation_derived_source",
                column: "file_reference_id");

            migrationBuilder.CreateIndex(
                name: "IX_template_generation_derived_source_parent_source_id_parent_first_page_parent_last_page_derived_content_sha256",
                table: "template_generation_derived_source",
                columns: new[] { "parent_source_id", "parent_first_page", "parent_last_page", "derived_content_sha256" });

            migrationBuilder.CreateIndex(
                name: "IX_template_generation_derived_source_unit_id",
                table: "template_generation_derived_source",
                column: "unit_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_template_generation_unit_batch_id_sequence",
                table: "template_generation_unit",
                columns: new[] { "batch_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_template_generation_unit_batch_id_status",
                table: "template_generation_unit",
                columns: new[] { "batch_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_template_generation_unit_created_template_id",
                table: "template_generation_unit",
                column: "created_template_id");

            migrationBuilder.CreateIndex(
                name: "IX_template_generation_unit_created_template_version_id",
                table: "template_generation_unit",
                column: "created_template_version_id",
                unique: true,
                filter: "\"created_template_version_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_template_generation_unit_extraction_job_id",
                table: "template_generation_unit",
                column: "extraction_job_id",
                unique: true,
                filter: "\"extraction_job_id\" IS NOT NULL");

            migrationBuilder.Sql(
                TemplateVersionIntegrityTriggerCatalog
                    .Schema17PublishedVersionContentImmutableStatement);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                TemplateVersionIntegrityTriggerCatalog
                    .DropPublishedVersionContentImmutableStatement);

            migrationBuilder.DropTable(
                name: "template_generation_derived_source");

            migrationBuilder.DropTable(
                name: "template_generation_unit");

            migrationBuilder.DropTable(
                name: "template_generation_batch");

            migrationBuilder.DropIndex(
                name: "IX_template_version_originating_batch_id",
                table: "template_version");

            migrationBuilder.DropIndex(
                name: "IX_template_version_originating_unit_id",
                table: "template_version");

            // EF's SQLite DropColumn operation requires a table rebuild, which
            // breaks triggers in other tables that reference template_version.
            // Modern SQLite supports native DROP COLUMN; with the one trigger
            // that references these columns removed, unrelated live triggers
            // remain valid throughout the downgrade.
            foreach (var columnName in TemplateVersionGenerationColumns)
            {
                migrationBuilder.Sql(
                    $"ALTER TABLE \"template_version\" " +
                    $"DROP COLUMN \"{columnName}\";");
            }

            migrationBuilder.Sql(
                TemplateVersionIntegrityTriggerCatalog
                    .Schema16PublishedVersionContentImmutableStatement);
        }

        private static readonly string[] TemplateVersionGenerationColumns =
        [
            "answer_style",
            "generation_profile_hash",
            "generation_profile_json",
            "generation_profile_version",
            "originating_batch_id",
            "originating_unit_id",
            "printed_test_name",
            "prompt_system",
            "resolved_grade",
            "step_set_index",
            "step_variation_index",
            "test_type",
        ];
    }
}
