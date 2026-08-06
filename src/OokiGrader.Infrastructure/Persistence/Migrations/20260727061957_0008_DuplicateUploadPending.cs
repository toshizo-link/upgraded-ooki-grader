using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OokiGrader.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0008_DuplicateUploadPending : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_upload_session_state",
                table: "upload_session");

            migrationBuilder.AddCheckConstraint(
                name: "ck_upload_session_state",
                table: "upload_session",
                sql: "state IN ('uploading','finalizing','duplicate_pending','completed','cancelled','expired','failed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_upload_session_state",
                table: "upload_session");

            migrationBuilder.AddCheckConstraint(
                name: "ck_upload_session_state",
                table: "upload_session",
                sql: "state IN ('uploading','finalizing','completed','cancelled','expired','failed')");
        }
    }
}
