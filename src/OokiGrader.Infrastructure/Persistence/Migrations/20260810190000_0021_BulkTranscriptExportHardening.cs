using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OokiGrader.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0021_BulkTranscriptExportHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "request_fingerprint",
                table: "bulk_transcript_export",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "request_idempotency_key",
                table: "bulk_transcript_export",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_bulk_transcript_export_created_by_staff_user_id_request_idempotency_key",
                table: "bulk_transcript_export",
                columns: new[] { "created_by_staff_user_id", "request_idempotency_key" },
                unique: true,
                filter: "\"request_idempotency_key\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_bulk_transcript_export_created_by_staff_user_id_state",
                table: "bulk_transcript_export",
                columns: new[] { "created_by_staff_user_id", "state" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_bulk_transcript_export_created_by_staff_user_id_request_idempotency_key",
                table: "bulk_transcript_export");

            migrationBuilder.DropIndex(
                name: "IX_bulk_transcript_export_created_by_staff_user_id_state",
                table: "bulk_transcript_export");

            migrationBuilder.DropColumn(
                name: "request_fingerprint",
                table: "bulk_transcript_export");

            migrationBuilder.DropColumn(
                name: "request_idempotency_key",
                table: "bulk_transcript_export");
        }
    }
}
