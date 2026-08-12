using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OokiGrader.Infrastructure.Persistence;

#nullable disable

namespace OokiGrader.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OokiGraderDbContext))]
[Migration("20260810030000_0018_OrderedScanAssembly")]
public partial class _0018_OrderedScanAssembly : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Keep every existing table in place. In particular, rebuilding
        // template_version, submission, or upload_session would discard live
        // SQLite triggers that refer to those tables.
        migrationBuilder.Sql(
            TemplateVersionIntegrityTriggerCatalog
                .DropPublishedVersionContentImmutableStatement);

        migrationBuilder.AddColumn<string>(
            name: "ordered_scan_batch_id",
            table: "upload_session",
            type: "TEXT",
            fixedLength: true,
            maxLength: 26,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ordered_scan_client_item_id",
            table: "upload_session",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "ordered_scan_input_ordinal",
            table: "upload_session",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "expected_submission_page_count",
            table: "template_version",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "assembly_manifest_hash",
            table: "submission",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ordered_scan_batch_id",
            table: "submission",
            type: "TEXT",
            fixedLength: true,
            maxLength: 26,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "ordered_scan_group_ordinal",
            table: "submission",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "ordered_scan_batch",
            columns: table => new
            {
                id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                test_session_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                expected_page_count = table.Column<int>(type: "INTEGER", nullable: false),
                status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                assembly_policy_version = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                plan_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                last_error_code = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                last_error_json = table.Column<string>(type: "TEXT", maxLength: 16000, nullable: true),
                created_by_staff_user_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                expires_at = table.Column<long>(type: "INTEGER", nullable: false),
                created_at = table.Column<long>(type: "INTEGER", nullable: false),
                updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                revision = table.Column<long>(type: "INTEGER", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ordered_scan_batch", x => x.id);
                table.CheckConstraint(
                    "ck_ordered_scan_batch_expected_pages",
                    "expected_page_count > 0");
                table.CheckConstraint(
                    "ck_ordered_scan_batch_expiry",
                    "expires_at > created_at");
                table.CheckConstraint(
                    "ck_ordered_scan_batch_status",
                    "status IN ('draft','processing','completed','needsReview'," +
                    "'failed','cancelled','expired')");
                table.ForeignKey(
                    name: "FK_ordered_scan_batch_staff_user_created_by_staff_user_id",
                    column: x => x.created_by_staff_user_id,
                    principalTable: "staff_user",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_ordered_scan_batch_test_session_test_session_id",
                    column: x => x.test_session_id,
                    principalTable: "test_session",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "ordered_scan_item",
            columns: table => new
            {
                id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                batch_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                input_ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                client_item_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                original_file_name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                upload_session_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                source_file_reference_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                source_sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                source_bytes = table.Column<long>(type: "INTEGER", nullable: true),
                upload_completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                detected_template_page_number = table.Column<int>(type: "INTEGER", nullable: true),
                classification_confidence_basis_points = table.Column<int>(type: "INTEGER", nullable: true),
                status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                group_ordinal = table.Column<int>(type: "INTEGER", nullable: true),
                submission_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                submission_page_number = table.Column<int>(type: "INTEGER", nullable: true),
                issue_code = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                created_at = table.Column<long>(type: "INTEGER", nullable: false),
                updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                revision = table.Column<long>(type: "INTEGER", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ordered_scan_item", x => x.id);
                table.CheckConstraint(
                    "ck_ordered_scan_item_confidence",
                    "classification_confidence_basis_points IS NULL OR " +
                    "classification_confidence_basis_points BETWEEN 0 AND 10000");
                table.CheckConstraint(
                    "ck_ordered_scan_item_detected_page",
                    "detected_template_page_number IS NULL OR " +
                    "detected_template_page_number > 0");
                table.CheckConstraint(
                    "ck_ordered_scan_item_grouped",
                    "status <> 'grouped' OR " +
                    "(submission_id IS NOT NULL AND submission_page_number > 0)");
                table.CheckConstraint(
                    "ck_ordered_scan_item_ordinal",
                    "input_ordinal > 0");
                table.CheckConstraint(
                    "ck_ordered_scan_item_source_manifest",
                    "(source_sha256 IS NULL OR length(source_sha256) = 64) " +
                    "AND (source_bytes IS NULL OR source_bytes > 0) AND " +
                    "(status NOT IN ('uploaded','classified','grouped','needsReview') " +
                    "OR (upload_session_id IS NOT NULL " +
                    "AND source_sha256 IS NOT NULL AND source_bytes > 0 " +
                    "AND upload_completed_at IS NOT NULL)) AND " +
                    "(status NOT IN ('uploaded','classified','needsReview') OR " +
                    "source_file_reference_id IS NOT NULL)");
                table.CheckConstraint(
                    "ck_ordered_scan_item_status",
                    "status IN ('pending','uploaded','classified','grouped'," +
                    "'needsReview','rejected')");
                table.CheckConstraint(
                    "ck_ordered_scan_item_submission_placement",
                    "(submission_id IS NULL AND submission_page_number IS NULL) OR " +
                    "(submission_id IS NOT NULL AND submission_page_number > 0 " +
                    "AND group_ordinal > 0)");
                table.ForeignKey(
                    name: "FK_ordered_scan_item_file_reference_source_file_reference_id",
                    column: x => x.source_file_reference_id,
                    principalTable: "file_reference",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_ordered_scan_item_ordered_scan_batch_batch_id",
                    column: x => x.batch_id,
                    principalTable: "ordered_scan_batch",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_ordered_scan_item_submission_submission_id",
                    column: x => x.submission_id,
                    principalTable: "submission",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_ordered_scan_item_upload_session_upload_session_id",
                    column: x => x.upload_session_id,
                    principalTable: "upload_session",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "submission_source_page",
            columns: table => new
            {
                id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                submission_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                page_number = table.Column<int>(type: "INTEGER", nullable: false),
                ordered_scan_item_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                upload_session_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                file_reference_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: true),
                source_page_number = table.Column<int>(type: "INTEGER", nullable: false),
                source_sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                assembly_policy_version = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                created_at = table.Column<long>(type: "INTEGER", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_submission_source_page", x => x.id);
                table.CheckConstraint(
                    "ck_submission_source_page_numbers",
                    "page_number > 0 AND source_page_number = 1");
                table.CheckConstraint(
                    "ck_submission_source_page_sha256",
                    "length(source_sha256) = 64");
                table.ForeignKey(
                    name: "FK_submission_source_page_file_reference_file_reference_id",
                    column: x => x.file_reference_id,
                    principalTable: "file_reference",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_submission_source_page_ordered_scan_item_ordered_scan_item_id",
                    column: x => x.ordered_scan_item_id,
                    principalTable: "ordered_scan_item",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_submission_source_page_submission_submission_id",
                    column: x => x.submission_id,
                    principalTable: "submission",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_submission_source_page_upload_session_upload_session_id",
                    column: x => x.upload_session_id,
                    principalTable: "upload_session",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_upload_session_ordered_scan_batch_id_ordered_scan_client_item_id",
            table: "upload_session",
            columns: ["ordered_scan_batch_id", "ordered_scan_client_item_id"],
            unique: true,
            filter: "\"ordered_scan_batch_id\" IS NOT NULL AND " +
                "\"state\" IN ('uploading','finalizing','duplicate_pending','completed')");

        migrationBuilder.CreateIndex(
            name: "IX_upload_session_ordered_scan_batch_id_ordered_scan_input_ordinal",
            table: "upload_session",
            columns: ["ordered_scan_batch_id", "ordered_scan_input_ordinal"],
            unique: true,
            filter: "\"ordered_scan_batch_id\" IS NOT NULL AND " +
                "\"state\" IN ('uploading','finalizing','duplicate_pending','completed')");

        migrationBuilder.CreateIndex(
            name: "IX_submission_ordered_scan_batch_id_assembly_manifest_hash",
            table: "submission",
            columns: ["ordered_scan_batch_id", "assembly_manifest_hash"],
            unique: true,
            filter: "\"ordered_scan_batch_id\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_submission_ordered_scan_batch_id_ordered_scan_group_ordinal",
            table: "submission",
            columns: ["ordered_scan_batch_id", "ordered_scan_group_ordinal"],
            unique: true,
            filter: "\"ordered_scan_batch_id\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_ordered_scan_batch_created_by_staff_user_id",
            table: "ordered_scan_batch",
            column: "created_by_staff_user_id");

        migrationBuilder.CreateIndex(
            name: "IX_ordered_scan_batch_status_expires_at",
            table: "ordered_scan_batch",
            columns: ["status", "expires_at"]);

        migrationBuilder.CreateIndex(
            name: "IX_ordered_scan_batch_test_session_id_status_created_at",
            table: "ordered_scan_batch",
            columns: ["test_session_id", "status", "created_at"],
            descending: [false, false, true]);

        migrationBuilder.CreateIndex(
            name: "IX_ordered_scan_item_batch_id_client_item_id",
            table: "ordered_scan_item",
            columns: ["batch_id", "client_item_id"],
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ordered_scan_item_batch_id_input_ordinal",
            table: "ordered_scan_item",
            columns: ["batch_id", "input_ordinal"],
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ordered_scan_item_batch_id_status",
            table: "ordered_scan_item",
            columns: ["batch_id", "status"]);

        migrationBuilder.CreateIndex(
            name: "IX_ordered_scan_item_source_file_reference_id",
            table: "ordered_scan_item",
            column: "source_file_reference_id",
            unique: true,
            filter: "\"source_file_reference_id\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_ordered_scan_item_submission_id_submission_page_number",
            table: "ordered_scan_item",
            columns: ["submission_id", "submission_page_number"],
            unique: true,
            filter: "\"submission_id\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_ordered_scan_item_upload_session_id",
            table: "ordered_scan_item",
            column: "upload_session_id",
            unique: true,
            filter: "\"upload_session_id\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_submission_source_page_file_reference_id",
            table: "submission_source_page",
            column: "file_reference_id",
            unique: true,
            filter: "\"file_reference_id\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_submission_source_page_ordered_scan_item_id",
            table: "submission_source_page",
            column: "ordered_scan_item_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_submission_source_page_submission_id_page_number",
            table: "submission_source_page",
            columns: ["submission_id", "page_number"],
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_submission_source_page_upload_session_id",
            table: "submission_source_page",
            column: "upload_session_id");

        migrationBuilder.Sql(CreateValidationTriggersSql);
        migrationBuilder.Sql(
            TemplateVersionIntegrityTriggerCatalog
                .Schema18PublishedVersionContentImmutableStatement);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            TemplateVersionIntegrityTriggerCatalog
                .DropPublishedVersionContentImmutableStatement);
        migrationBuilder.Sql(DropValidationTriggersSql);

        migrationBuilder.DropTable(name: "submission_source_page");
        migrationBuilder.DropTable(name: "ordered_scan_item");

        migrationBuilder.DropIndex(
            name: "IX_upload_session_ordered_scan_batch_id_ordered_scan_client_item_id",
            table: "upload_session");
        migrationBuilder.DropIndex(
            name: "IX_upload_session_ordered_scan_batch_id_ordered_scan_input_ordinal",
            table: "upload_session");
        migrationBuilder.DropIndex(
            name: "IX_submission_ordered_scan_batch_id_assembly_manifest_hash",
            table: "submission");
        migrationBuilder.DropIndex(
            name: "IX_submission_ordered_scan_batch_id_ordered_scan_group_ordinal",
            table: "submission");

        migrationBuilder.DropTable(name: "ordered_scan_batch");

        foreach (var column in UploadSessionColumns)
        {
            migrationBuilder.Sql(
                $"ALTER TABLE \"upload_session\" DROP COLUMN \"{column}\";");
        }

        foreach (var column in SubmissionColumns)
        {
            migrationBuilder.Sql(
                $"ALTER TABLE \"submission\" DROP COLUMN \"{column}\";");
        }

        migrationBuilder.Sql(
            "ALTER TABLE \"template_version\" " +
            "DROP COLUMN \"expected_submission_page_count\";");
        migrationBuilder.Sql(
            TemplateVersionIntegrityTriggerCatalog
                .Schema17PublishedVersionContentImmutableStatement);
    }

    private const string CreateValidationTriggersSql =
        """
        CREATE TRIGGER trg_template_version_expected_submission_pages_insert
        BEFORE INSERT ON template_version
        WHEN NEW.expected_submission_page_count IS NOT NULL
          AND NEW.expected_submission_page_count <= 0
        BEGIN
            SELECT RAISE(ABORT, 'expected_submission_page_count_must_be_positive');
        END;

        CREATE TRIGGER trg_template_version_expected_submission_pages_update
        BEFORE UPDATE OF expected_submission_page_count ON template_version
        WHEN NEW.expected_submission_page_count IS NOT NULL
          AND NEW.expected_submission_page_count <= 0
        BEGIN
            SELECT RAISE(ABORT, 'expected_submission_page_count_must_be_positive');
        END;

        CREATE TRIGGER trg_upload_session_ordered_scan_binding_insert
        BEFORE INSERT ON upload_session
        WHEN NOT (
            (NEW.ordered_scan_batch_id IS NULL
             AND NEW.ordered_scan_input_ordinal IS NULL
             AND NEW.ordered_scan_client_item_id IS NULL)
            OR
            (NEW.ordered_scan_batch_id IS NOT NULL
             AND NEW.ordered_scan_input_ordinal > 0
             AND NEW.ordered_scan_client_item_id IS NOT NULL
             AND EXISTS (
                 SELECT 1 FROM ordered_scan_batch
                 WHERE id = NEW.ordered_scan_batch_id)))
        BEGIN
            SELECT RAISE(ABORT, 'invalid_ordered_scan_upload_binding');
        END;

        CREATE TRIGGER trg_upload_session_ordered_scan_binding_update
        BEFORE UPDATE OF ordered_scan_batch_id, ordered_scan_input_ordinal,
            ordered_scan_client_item_id ON upload_session
        WHEN NOT (
            (NEW.ordered_scan_batch_id IS NULL
             AND NEW.ordered_scan_input_ordinal IS NULL
             AND NEW.ordered_scan_client_item_id IS NULL)
            OR
            (NEW.ordered_scan_batch_id IS NOT NULL
             AND NEW.ordered_scan_input_ordinal > 0
             AND NEW.ordered_scan_client_item_id IS NOT NULL
             AND EXISTS (
                 SELECT 1 FROM ordered_scan_batch
                 WHERE id = NEW.ordered_scan_batch_id)))
        BEGIN
            SELECT RAISE(ABORT, 'invalid_ordered_scan_upload_binding');
        END;

        CREATE TRIGGER trg_submission_ordered_scan_provenance_insert
        BEFORE INSERT ON submission
        WHEN NOT (
            (NEW.ordered_scan_batch_id IS NULL
             AND NEW.ordered_scan_group_ordinal IS NULL
             AND NEW.assembly_manifest_hash IS NULL)
            OR
            (NEW.ordered_scan_batch_id IS NOT NULL
             AND NEW.ordered_scan_group_ordinal > 0
             AND length(NEW.assembly_manifest_hash) = 64
             AND EXISTS (
                 SELECT 1 FROM ordered_scan_batch
                 WHERE id = NEW.ordered_scan_batch_id)))
        BEGIN
            SELECT RAISE(ABORT, 'invalid_ordered_scan_submission_provenance');
        END;

        CREATE TRIGGER trg_submission_ordered_scan_provenance_update
        BEFORE UPDATE OF ordered_scan_batch_id, ordered_scan_group_ordinal,
            assembly_manifest_hash ON submission
        WHEN NOT (
            (NEW.ordered_scan_batch_id IS NULL
             AND NEW.ordered_scan_group_ordinal IS NULL
             AND NEW.assembly_manifest_hash IS NULL)
            OR
            (NEW.ordered_scan_batch_id IS NOT NULL
             AND NEW.ordered_scan_group_ordinal > 0
             AND length(NEW.assembly_manifest_hash) = 64
             AND EXISTS (
                 SELECT 1 FROM ordered_scan_batch
                 WHERE id = NEW.ordered_scan_batch_id)))
        BEGIN
            SELECT RAISE(ABORT, 'invalid_ordered_scan_submission_provenance');
        END;

        CREATE TRIGGER trg_ordered_scan_batch_restrict_delete
        BEFORE DELETE ON ordered_scan_batch
        WHEN EXISTS (
            SELECT 1 FROM upload_session
            WHERE ordered_scan_batch_id = OLD.id)
          OR EXISTS (
            SELECT 1 FROM submission
            WHERE ordered_scan_batch_id = OLD.id)
        BEGIN
            SELECT RAISE(ABORT, 'ordered_scan_batch_is_referenced');
        END;

        CREATE TRIGGER trg_submission_source_page_lineage_update
        BEFORE UPDATE ON submission_source_page
        WHEN NEW.id IS NOT OLD.id
          OR NEW.submission_id IS NOT OLD.submission_id
          OR NEW.page_number IS NOT OLD.page_number
          OR NEW.ordered_scan_item_id IS NOT OLD.ordered_scan_item_id
          OR NEW.upload_session_id IS NOT OLD.upload_session_id
          OR NEW.source_page_number IS NOT OLD.source_page_number
          OR NEW.source_sha256 IS NOT OLD.source_sha256
          OR NEW.assembly_policy_version IS NOT OLD.assembly_policy_version
          OR NEW.created_at IS NOT OLD.created_at
          OR OLD.file_reference_id IS NULL
          OR NEW.file_reference_id IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'submission_source_page_lineage_is_immutable');
        END;

        CREATE TRIGGER trg_submission_source_page_lineage_delete
        BEFORE DELETE ON submission_source_page
        BEGIN
            SELECT RAISE(ABORT, 'submission_source_page_lineage_cannot_be_deleted');
        END;
        """;

    private const string DropValidationTriggersSql =
        """
        DROP TRIGGER IF EXISTS trg_template_version_expected_submission_pages_insert;
        DROP TRIGGER IF EXISTS trg_template_version_expected_submission_pages_update;
        DROP TRIGGER IF EXISTS trg_upload_session_ordered_scan_binding_insert;
        DROP TRIGGER IF EXISTS trg_upload_session_ordered_scan_binding_update;
        DROP TRIGGER IF EXISTS trg_submission_ordered_scan_provenance_insert;
        DROP TRIGGER IF EXISTS trg_submission_ordered_scan_provenance_update;
        DROP TRIGGER IF EXISTS trg_ordered_scan_batch_restrict_delete;
        DROP TRIGGER IF EXISTS trg_submission_source_page_lineage_update;
        DROP TRIGGER IF EXISTS trg_submission_source_page_lineage_delete;
        """;

    private static readonly string[] UploadSessionColumns =
    [
        "ordered_scan_batch_id",
        "ordered_scan_client_item_id",
        "ordered_scan_input_ordinal",
    ];

    private static readonly string[] SubmissionColumns =
    [
        "assembly_manifest_hash",
        "ordered_scan_batch_id",
        "ordered_scan_group_ordinal",
    ];
}
