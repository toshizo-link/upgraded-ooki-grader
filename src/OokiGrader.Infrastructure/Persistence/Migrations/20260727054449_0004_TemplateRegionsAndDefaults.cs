using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OokiGrader.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0004_TemplateRegionsAndDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP TRIGGER IF EXISTS trg_published_template_version_content_immutable;");

            migrationBuilder.DropCheckConstraint(
                name: "ck_template_version_points",
                table: "template_version");

            migrationBuilder.AddColumn<long>(
                name: "default_points_milli",
                table: "test_template",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1000L);

            migrationBuilder.AddColumn<long>(
                name: "default_points_milli",
                table: "template_version",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1000L);

            migrationBuilder.AddColumn<string>(
                name: "rubric_text",
                table: "question",
                type: "TEXT",
                maxLength: 20000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "teacher_note",
                table: "question",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "region",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    owner_type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    owner_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    page_number = table.Column<int>(type: "INTEGER", nullable: false),
                    region_type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    x_millionths = table.Column<int>(type: "INTEGER", nullable: false),
                    y_millionths = table.Column<int>(type: "INTEGER", nullable: false),
                    width_millionths = table.Column<int>(type: "INTEGER", nullable: false),
                    height_millionths = table.Column<int>(type: "INTEGER", nullable: false),
                    rotation_degrees = table.Column<int>(type: "INTEGER", nullable: false),
                    created_source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    confidence_basis_points = table.Column<int>(type: "INTEGER", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_region", x => x.id);
                    table.CheckConstraint("ck_region_bounds", "page_number > 0 AND x_millionths >= 0 AND y_millionths >= 0 AND width_millionths > 0 AND height_millionths > 0 AND x_millionths + width_millionths <= 1000000 AND y_millionths + height_millionths <= 1000000");
                    table.CheckConstraint("ck_region_confidence", "confidence_basis_points IS NULL OR confidence_basis_points BETWEEN 0 AND 10000");
                    table.CheckConstraint("ck_region_owner", "owner_type = 'question'");
                    table.CheckConstraint("ck_region_rotation", "rotation_degrees IN (0,90,180,270)");
                    table.CheckConstraint("ck_region_type", "region_type IN ('question','answer','name','student_number','ignore','anchor')");
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_test_template_default_points",
                table: "test_template",
                sql: "default_points_milli > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_template_version_points",
                table: "template_version",
                sql: "(target_total_points_milli IS NULL OR target_total_points_milli >= 0) AND default_points_milli > 0");

            migrationBuilder.CreateIndex(
                name: "IX_question_answer_region_id",
                table: "question",
                column: "answer_region_id");

            migrationBuilder.CreateIndex(
                name: "IX_question_question_region_id",
                table: "question",
                column: "question_region_id");

            migrationBuilder.CreateIndex(
                name: "IX_region_owner_type_owner_id_region_type",
                table: "region",
                columns: new[] { "owner_type", "owner_id", "region_type" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_question_region_answer_region_id",
                table: "question",
                column: "answer_region_id",
                principalTable: "region",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_question_region_question_region_id",
                table: "question",
                column: "question_region_id",
                principalTable: "region",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP TRIGGER IF EXISTS trg_published_template_version_content_immutable;");

            migrationBuilder.DropForeignKey(
                name: "FK_question_region_answer_region_id",
                table: "question");

            migrationBuilder.DropForeignKey(
                name: "FK_question_region_question_region_id",
                table: "question");

            migrationBuilder.DropTable(
                name: "region");

            migrationBuilder.DropCheckConstraint(
                name: "ck_test_template_default_points",
                table: "test_template");

            migrationBuilder.DropCheckConstraint(
                name: "ck_template_version_points",
                table: "template_version");

            migrationBuilder.DropIndex(
                name: "IX_question_answer_region_id",
                table: "question");

            migrationBuilder.DropIndex(
                name: "IX_question_question_region_id",
                table: "question");

            migrationBuilder.DropColumn(
                name: "default_points_milli",
                table: "test_template");

            migrationBuilder.DropColumn(
                name: "default_points_milli",
                table: "template_version");

            migrationBuilder.DropColumn(
                name: "rubric_text",
                table: "question");

            migrationBuilder.DropColumn(
                name: "teacher_note",
                table: "question");

            migrationBuilder.AddCheckConstraint(
                name: "ck_template_version_points",
                table: "template_version",
                sql: "target_total_points_milli IS NULL OR target_total_points_milli >= 0");
        }
    }
}
