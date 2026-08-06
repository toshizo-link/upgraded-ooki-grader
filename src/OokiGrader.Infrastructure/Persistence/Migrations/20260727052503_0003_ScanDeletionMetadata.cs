using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OokiGrader.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0003_ScanDeletionMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "scan_deleted_at",
                table: "submission",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "scan_deletion_reason",
                table: "submission",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE submission
                SET scan_deleted_at = COALESCE(
                        (
                            SELECT MAX(item.deleted_at)
                            FROM deletion_manifest_item AS item
                            WHERE item.submission_id = submission.id
                              AND item.deleted_at IS NOT NULL
                        ),
                        updated_at
                    ),
                    scan_deletion_reason = COALESCE(
                        (
                            SELECT manifest.reason
                            FROM deletion_manifest_item AS item
                            JOIN deletion_manifest AS manifest
                              ON manifest.id = item.deletion_manifest_id
                            WHERE item.submission_id = submission.id
                            ORDER BY item.deleted_at DESC
                            LIMIT 1
                        ),
                        'age'
                    )
                WHERE scan_payload_state = 'scan_deleted';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "scan_deleted_at",
                table: "submission");

            migrationBuilder.DropColumn(
                name: "scan_deletion_reason",
                table: "submission");
        }
    }
}
