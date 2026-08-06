using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OokiGrader.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0014_GeminiBatchCapabilityProbe : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_capability_probe_latency",
                table: "ai_capability_probe");

            migrationBuilder.AddColumn<long>(
                name: "last_batch_capability_probe_at",
                table: "ai_connection",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "last_batch_capability_probe_credential_revision",
                table: "ai_connection",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_batch_capability_probe_error_code",
                table: "ai_connection",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_batch_capability_probe_state",
                table: "ai_connection",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "batch_available",
                table: "ai_capability_probe",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "batch_cleanup_succeeded",
                table: "ai_capability_probe",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "batch_latency_milliseconds",
                table: "ai_capability_probe",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "batch_safe_error_code",
                table: "ai_capability_probe",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "batch_state",
                table: "ai_capability_probe",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_connection_batch_probe_revision",
                table: "ai_connection",
                sql: "last_batch_capability_probe_credential_revision IS NULL OR last_batch_capability_probe_credential_revision > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_capability_probe_batch_state",
                table: "ai_capability_probe",
                sql: "batch_state IN ('not_run','passed','failed')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_capability_probe_latency",
                table: "ai_capability_probe",
                sql: "(latency_milliseconds IS NULL OR latency_milliseconds >= 0) AND (batch_latency_milliseconds IS NULL OR batch_latency_milliseconds >= 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_connection_batch_probe_revision",
                table: "ai_connection");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_capability_probe_batch_state",
                table: "ai_capability_probe");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_capability_probe_latency",
                table: "ai_capability_probe");

            migrationBuilder.DropColumn(
                name: "last_batch_capability_probe_at",
                table: "ai_connection");

            migrationBuilder.DropColumn(
                name: "last_batch_capability_probe_credential_revision",
                table: "ai_connection");

            migrationBuilder.DropColumn(
                name: "last_batch_capability_probe_error_code",
                table: "ai_connection");

            migrationBuilder.DropColumn(
                name: "last_batch_capability_probe_state",
                table: "ai_connection");

            migrationBuilder.DropColumn(
                name: "batch_available",
                table: "ai_capability_probe");

            migrationBuilder.DropColumn(
                name: "batch_cleanup_succeeded",
                table: "ai_capability_probe");

            migrationBuilder.DropColumn(
                name: "batch_latency_milliseconds",
                table: "ai_capability_probe");

            migrationBuilder.DropColumn(
                name: "batch_safe_error_code",
                table: "ai_capability_probe");

            migrationBuilder.DropColumn(
                name: "batch_state",
                table: "ai_capability_probe");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_capability_probe_latency",
                table: "ai_capability_probe",
                sql: "latency_milliseconds IS NULL OR latency_milliseconds >= 0");
        }
    }
}
