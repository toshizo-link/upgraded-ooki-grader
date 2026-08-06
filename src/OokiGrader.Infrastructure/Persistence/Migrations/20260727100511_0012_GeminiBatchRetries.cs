using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OokiGrader.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0012_GeminiBatchRetries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ai_request_entity_type_entity_id_input_manifest_hash_task_profile_revision",
                table: "ai_request");

            migrationBuilder.AddColumn<int>(
                name: "attempt_number",
                table: "ai_request",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "retry_of_ai_request_id",
                table: "ai_request",
                type: "TEXT",
                fixedLength: true,
                maxLength: 26,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_request_entity_type_entity_id_input_manifest_hash_task_profile_revision_attempt_number",
                table: "ai_request",
                columns: new[] { "entity_type", "entity_id", "input_manifest_hash", "task_profile_revision", "attempt_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_request_retry_of_ai_request_id",
                table: "ai_request",
                column: "retry_of_ai_request_id",
                unique: true,
                filter: "\"retry_of_ai_request_id\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_request_attempt_number",
                table: "ai_request",
                sql: "attempt_number BETWEEN 1 AND 8");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ai_request_entity_type_entity_id_input_manifest_hash_task_profile_revision_attempt_number",
                table: "ai_request");

            migrationBuilder.DropIndex(
                name: "IX_ai_request_retry_of_ai_request_id",
                table: "ai_request");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_request_attempt_number",
                table: "ai_request");

            migrationBuilder.DropColumn(
                name: "attempt_number",
                table: "ai_request");

            migrationBuilder.DropColumn(
                name: "retry_of_ai_request_id",
                table: "ai_request");

            migrationBuilder.CreateIndex(
                name: "IX_ai_request_entity_type_entity_id_input_manifest_hash_task_profile_revision",
                table: "ai_request",
                columns: new[] { "entity_type", "entity_id", "input_manifest_hash", "task_profile_revision" },
                unique: true);
        }
    }
}
