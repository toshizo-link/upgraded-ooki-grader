using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OokiGrader.Infrastructure.Persistence;

#nullable disable

namespace OokiGrader.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OokiGraderDbContext))]
[Migration("20260810120000_0019_QuestionGradingFlags")]
public partial class _0019_QuestionGradingFlags : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // ALTER TABLE ADD COLUMN preserves the published-template integrity
        // triggers and gives every legacy question the compatible false/false
        // behavior.
        migrationBuilder.AddColumn<bool>(
            name: "answer_order_insensitive",
            table: "question",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "requires_complete_answer",
            table: "question",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // SQLite rebuilds question when dropping columns. Recreate the complete
        // schema-18 trigger set so a downgrade never loses published-question
        // immutability triggers.
        foreach (var statement in TemplateVersionIntegrityTriggerCatalog.DropStatements)
        {
            migrationBuilder.Sql(statement);
        }

        migrationBuilder.Sql(
            "ALTER TABLE \"question\" DROP COLUMN \"answer_order_insensitive\";");
        migrationBuilder.Sql(
            "ALTER TABLE \"question\" DROP COLUMN \"requires_complete_answer\";");

        foreach (var statement in TemplateVersionIntegrityTriggerCatalog.Schema18Statements)
        {
            migrationBuilder.Sql(statement);
        }
    }
}
