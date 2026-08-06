using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OokiGrader.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0002_RetentionManifests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "deletion_manifest",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    background_job_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                    reason = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    cutoff_at = table.Column<long>(type: "INTEGER", nullable: true),
                    planned_object_count = table.Column<int>(type: "INTEGER", nullable: false),
                    planned_reference_count = table.Column<int>(type: "INTEGER", nullable: false),
                    planned_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    deleted_object_count = table.Column<int>(type: "INTEGER", nullable: false),
                    released_reference_count = table.Column<int>(type: "INTEGER", nullable: false),
                    missing_object_count = table.Column<int>(type: "INTEGER", nullable: false),
                    failure_count = table.Column<int>(type: "INTEGER", nullable: false),
                    deleted_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    safe_error_detail = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    started_at = table.Column<long>(type: "INTEGER", nullable: true),
                    completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deletion_manifest", x => x.id);
                    table.CheckConstraint("ck_deletion_manifest_counts", "planned_object_count >= 0 AND planned_reference_count >= 0 AND planned_bytes >= 0 AND deleted_object_count >= 0 AND released_reference_count >= 0 AND missing_object_count >= 0 AND failure_count >= 0 AND deleted_bytes >= 0");
                    table.CheckConstraint("ck_deletion_manifest_reason", "reason IN ('age','quota','manual_erasure','orphan_cleanup')");
                    table.CheckConstraint("ck_deletion_manifest_state", "state IN ('pending','deleting','completed','failed')");
                    table.ForeignKey(
                        name: "FK_deletion_manifest_background_job_background_job_id",
                        column: x => x.background_job_id,
                        principalTable: "background_job",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "deletion_manifest_item",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    deletion_manifest_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    file_object_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    file_reference_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    submission_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    purpose = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    storage_class = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    extension = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    relative_object_path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    delete_physical_object = table.Column<bool>(type: "INTEGER", nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    outcome = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    error_code = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    attempt_count = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    deleted_at = table.Column<long>(type: "INTEGER", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deletion_manifest_item", x => x.id);
                    table.CheckConstraint("ck_deletion_manifest_item_attempts", "attempt_count >= 0");
                    table.CheckConstraint("ck_deletion_manifest_item_bytes", "bytes >= 0");
                    table.CheckConstraint("ck_deletion_manifest_item_state", "state IN ('pending','deleted','already_missing','reference_released','failed')");
                    table.ForeignKey(
                        name: "FK_deletion_manifest_item_deletion_manifest_deletion_manifest_id",
                        column: x => x.deletion_manifest_id,
                        principalTable: "deletion_manifest",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_deletion_manifest_item_file_object_file_object_id",
                        column: x => x.file_object_id,
                        principalTable: "file_object",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_deletion_manifest_background_job_id",
                table: "deletion_manifest",
                column: "background_job_id");

            migrationBuilder.CreateIndex(
                name: "IX_deletion_manifest_completed_at",
                table: "deletion_manifest",
                column: "completed_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_deletion_manifest_state_created_at_id",
                table: "deletion_manifest",
                columns: new[] { "state", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_deletion_manifest_item_deletion_manifest_id_file_reference_id",
                table: "deletion_manifest_item",
                columns: new[] { "deletion_manifest_id", "file_reference_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_deletion_manifest_item_deletion_manifest_id_state",
                table: "deletion_manifest_item",
                columns: new[] { "deletion_manifest_id", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_deletion_manifest_item_file_object_id",
                table: "deletion_manifest_item",
                column: "file_object_id");

            migrationBuilder.CreateIndex(
                name: "IX_deletion_manifest_item_submission_id",
                table: "deletion_manifest_item",
                column: "submission_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deletion_manifest_item");

            migrationBuilder.DropTable(
                name: "deletion_manifest");
        }
    }
}
