using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OokiGrader.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0025_SchoolManagerGuardianDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pass_fail_import",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    source_sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    source_file_name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    source_last_write_at = table.Column<long>(type: "INTEGER", nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    row_count = table.Column<int>(type: "INTEGER", nullable: false),
                    delivery_count = table.Column<int>(type: "INTEGER", nullable: false),
                    error_code = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    safe_error_detail = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    processed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pass_fail_import", x => x.id);
                    table.CheckConstraint("ck_pass_fail_import_counts", "row_count >= 0 AND delivery_count >= 0");
                    table.CheckConstraint("ck_pass_fail_import_state", "state IN ('processing','processed','failed')");
                });

            migrationBuilder.CreateTable(
                name: "school_manager_settings",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    base_url = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    username = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    password_secret_reference = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    credential_revision = table.Column<long>(type: "INTEGER", nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    dry_run = table.Column<bool>(type: "INTEGER", nullable: false),
                    activation_started_at = table.Column<long>(type: "INTEGER", nullable: true),
                    last_credential_tested_at = table.Column<long>(type: "INTEGER", nullable: true),
                    last_error_code = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    safe_error_detail = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_school_manager_settings", x => x.id);
                    table.CheckConstraint("ck_school_manager_settings_credential_revision", "credential_revision >= 0");
                    table.CheckConstraint("ck_school_manager_settings_enabled", "enabled = 0 OR (password_secret_reference IS NOT NULL AND activation_started_at IS NOT NULL)");
                    table.CheckConstraint("ck_school_manager_settings_singleton", "id = 'school-manager'");
                });

            migrationBuilder.CreateTable(
                name: "guardian_delivery",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    student_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    submission_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                    export_record_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                    pass_fail_import_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                    file_reference_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                    source_key = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    attachment_name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    not_before_at = table.Column<long>(type: "INTEGER", nullable: false),
                    attempt_count = table.Column<int>(type: "INTEGER", nullable: false),
                    max_attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    last_attempt_at = table.Column<long>(type: "INTEGER", nullable: true),
                    last_dry_run_at = table.Column<long>(type: "INTEGER", nullable: true),
                    sent_at = table.Column<long>(type: "INTEGER", nullable: true),
                    sent_local_date = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    school_manager_thread_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    last_error_code = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    safe_error_detail = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guardian_delivery", x => x.id);
                    table.CheckConstraint("ck_guardian_delivery_attempts", "attempt_count >= 0 AND max_attempts > 0");
                    table.CheckConstraint("ck_guardian_delivery_kind", "kind IN ('result_pdf','pass_fail_table')");
                    table.CheckConstraint("ck_guardian_delivery_pass_fail_file", "kind <> 'pass_fail_table' OR state = 'pending' OR file_reference_id IS NOT NULL");
                    table.CheckConstraint("ck_guardian_delivery_sent", "state <> 'sent' OR (sent_at IS NOT NULL AND school_manager_thread_id IS NOT NULL)");
                    table.CheckConstraint("ck_guardian_delivery_source", "(kind = 'result_pdf' AND submission_id IS NOT NULL AND export_record_id IS NOT NULL AND pass_fail_import_id IS NULL) OR (kind = 'pass_fail_table' AND submission_id IS NULL AND export_record_id IS NULL AND pass_fail_import_id IS NOT NULL)");
                    table.CheckConstraint("ck_guardian_delivery_state", "state IN ('pending','ready','sending','sent','failed','canceled')");
                    table.ForeignKey(
                        name: "FK_guardian_delivery_export_record_export_record_id",
                        column: x => x.export_record_id,
                        principalTable: "export_record",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_guardian_delivery_file_reference_file_reference_id",
                        column: x => x.file_reference_id,
                        principalTable: "file_reference",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_guardian_delivery_pass_fail_import_pass_fail_import_id",
                        column: x => x.pass_fail_import_id,
                        principalTable: "pass_fail_import",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_guardian_delivery_student_student_id",
                        column: x => x.student_id,
                        principalTable: "student",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_guardian_delivery_submission_submission_id",
                        column: x => x.submission_id,
                        principalTable: "submission",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_guardian_delivery_export_record_id",
                table: "guardian_delivery",
                column: "export_record_id",
                unique: true,
                filter: "\"export_record_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_guardian_delivery_file_reference_id",
                table: "guardian_delivery",
                column: "file_reference_id",
                unique: true,
                filter: "\"file_reference_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_guardian_delivery_kind_student_id_source_key",
                table: "guardian_delivery",
                columns: new[] { "kind", "student_id", "source_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_guardian_delivery_pass_fail_import_id",
                table: "guardian_delivery",
                column: "pass_fail_import_id");

            migrationBuilder.CreateIndex(
                name: "IX_guardian_delivery_state_not_before_at_created_at_id",
                table: "guardian_delivery",
                columns: new[] { "state", "not_before_at", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_guardian_delivery_student_id_kind_sent_local_date",
                table: "guardian_delivery",
                columns: new[] { "student_id", "kind", "sent_local_date" },
                unique: true,
                filter: "\"kind\" = 'pass_fail_table' AND \"sent_local_date\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_guardian_delivery_submission_id",
                table: "guardian_delivery",
                column: "submission_id");

            migrationBuilder.CreateIndex(
                name: "IX_pass_fail_import_source_sha256",
                table: "pass_fail_import",
                column: "source_sha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pass_fail_import_state_created_at_id",
                table: "pass_fail_import",
                columns: new[] { "state", "created_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "guardian_delivery");

            migrationBuilder.DropTable(
                name: "school_manager_settings");

            migrationBuilder.DropTable(
                name: "pass_fail_import");
        }
    }
}
