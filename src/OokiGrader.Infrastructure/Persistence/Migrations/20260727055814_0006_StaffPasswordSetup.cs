using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OokiGrader.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0006_StaffPasswordSetup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "must_change_password",
                table: "staff_user",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "password_setup_expires_at",
                table: "staff_user",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "password_setup_used_at",
                table: "staff_user",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "must_change_password",
                table: "staff_user");

            migrationBuilder.DropColumn(
                name: "password_setup_expires_at",
                table: "staff_user");

            migrationBuilder.DropColumn(
                name: "password_setup_used_at",
                table: "staff_user");
        }
    }
}
