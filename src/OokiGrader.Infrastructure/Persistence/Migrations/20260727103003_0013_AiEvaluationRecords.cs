using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OokiGrader.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0013_AiEvaluationRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_evaluation_record",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    ai_task_profile_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    task_profile_revision = table.Column<long>(type: "INTEGER", nullable: false),
                    provider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    model_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    connection_revision = table.Column<long>(type: "INTEGER", nullable: false),
                    task_type = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    processing_strategy = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    prompt_version = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    schema_version = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    prompt_content_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    dataset_version = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    dataset_sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    evidence_sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    sample_count = table.Column<int>(type: "INTEGER", nullable: false),
                    agreement_basis_points = table.Column<int>(type: "INTEGER", nullable: false),
                    lower_confidence_bound_basis_points = table.Column<int>(type: "INTEGER", nullable: false),
                    critical_failure_count = table.Column<int>(type: "INTEGER", nullable: false),
                    teacher_review_only = table.Column<bool>(type: "INTEGER", nullable: false),
                    signed_off_by_staff_user_id = table.Column<string>(type: "TEXT", fixedLength: true, maxLength: 26, nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_evaluation_record", x => x.id);
                    table.CheckConstraint("ck_ai_evaluation_record_accuracy", "agreement_basis_points BETWEEN 0 AND 10000 AND lower_confidence_bound_basis_points BETWEEN 0 AND 10000 AND lower_confidence_bound_basis_points <= agreement_basis_points");
                    table.CheckConstraint("ck_ai_evaluation_record_provider", "provider = 'geminiDirect'");
                    table.CheckConstraint("ck_ai_evaluation_record_revisions", "task_profile_revision > 0 AND connection_revision > 0");
                    table.CheckConstraint("ck_ai_evaluation_record_sample", "sample_count > 0 AND critical_failure_count >= 0 AND critical_failure_count <= sample_count");
                    table.CheckConstraint("ck_ai_evaluation_record_strategy", "processing_strategy IN ('gemini_batch','queued_standard','expedite_standard')");
                    table.CheckConstraint("ck_ai_evaluation_record_task", "task_type IN ('templateExtraction','nameTranscription','initialGrading','adjudication')");
                    table.ForeignKey(
                        name: "FK_ai_evaluation_record_ai_task_profile_ai_task_profile_id",
                        column: x => x.ai_task_profile_id,
                        principalTable: "ai_task_profile",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ai_evaluation_record_staff_user_signed_off_by_staff_user_id",
                        column: x => x.signed_off_by_staff_user_id,
                        principalTable: "staff_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_evaluation_record_ai_task_profile_id_created_at",
                table: "ai_evaluation_record",
                columns: new[] { "ai_task_profile_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ai_evaluation_record_ai_task_profile_id_task_profile_revision_evidence_sha256",
                table: "ai_evaluation_record",
                columns: new[] { "ai_task_profile_id", "task_profile_revision", "evidence_sha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_evaluation_record_signed_off_by_staff_user_id",
                table: "ai_evaluation_record",
                column: "signed_off_by_staff_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_evaluation_record");
        }
    }
}
