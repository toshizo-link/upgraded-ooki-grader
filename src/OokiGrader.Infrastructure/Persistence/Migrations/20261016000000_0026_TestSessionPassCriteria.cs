using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OokiGrader.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OokiGraderDbContext))]
[Migration("20261016000000_0026_TestSessionPassCriteria")]
public sealed class TestSessionPassCriteria : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<int>(
            name: "maximum_mistakes_to_pass", table: "test_session",
            type: "INTEGER", nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        // Keep independently managed SQLite triggers intact; no rebuild is needed.
        migrationBuilder.Sql("ALTER TABLE test_session DROP COLUMN maximum_mistakes_to_pass;");
}
