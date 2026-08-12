using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OokiGrader.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0020_BulkTranscriptExports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bulk_transcript_export",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    background_job_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    selector_json = table.Column<string>(type: "TEXT", maxLength: 32000, nullable: false),
                    selector_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    source_snapshot_json = table.Column<string>(type: "TEXT", maxLength: 512000, nullable: false),
                    source_fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    renderer_version = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    package_format_version = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    student_count = table.Column<int>(type: "INTEGER", nullable: false),
                    result_count = table.Column<int>(type: "INTEGER", nullable: false),
                    processed_result_count = table.Column<int>(type: "INTEGER", nullable: false),
                    file_reference_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                    sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    bytes = table.Column<long>(type: "INTEGER", nullable: true),
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
                    table.PrimaryKey("PK_bulk_transcript_export", x => x.id);
                    table.CheckConstraint("ck_bulk_transcript_export_counts", "student_count > 0 AND student_count <= result_count AND result_count > 0 AND processed_result_count >= 0 AND processed_result_count <= result_count");
                    table.CheckConstraint("ck_bulk_transcript_export_state", "state IN ('queued','rendering','verified','failed','superseded')");
                    table.CheckConstraint("ck_bulk_transcript_export_superseded", "superseded_at IS NULL OR superseded_reason IS NOT NULL");
                    table.CheckConstraint("ck_bulk_transcript_export_verified", "state <> 'verified' OR (file_reference_id IS NOT NULL AND sha256 IS NOT NULL AND bytes > 0 AND completed_at IS NOT NULL AND processed_result_count = result_count)");
                    table.ForeignKey(
                        name: "FK_bulk_transcript_export_background_job_background_job_id",
                        column: x => x.background_job_id,
                        principalTable: "background_job",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bulk_transcript_export_file_reference_file_reference_id",
                        column: x => x.file_reference_id,
                        principalTable: "file_reference",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bulk_transcript_export_staff_user_created_by_staff_user_id",
                        column: x => x.created_by_staff_user_id,
                        principalTable: "staff_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bulk_transcript_export_background_job_id",
                table: "bulk_transcript_export",
                column: "background_job_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bulk_transcript_export_created_by_staff_user_id_created_at_id",
                table: "bulk_transcript_export",
                columns: new[] { "created_by_staff_user_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_bulk_transcript_export_file_reference_id",
                table: "bulk_transcript_export",
                column: "file_reference_id",
                unique: true,
                filter: "\"file_reference_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_bulk_transcript_export_state_created_at_id",
                table: "bulk_transcript_export",
                columns: new[] { "state", "created_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bulk_transcript_export");
        }
    }
}
