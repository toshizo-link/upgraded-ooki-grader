namespace OokiGrader.Infrastructure.Persistence;

/// <summary>
/// Versioned trigger definitions used by migrations and upgrade tests while
/// the SQLite schema evolves. Do not change an existing schema set when adding
/// columns in a later migration; add a new schema set instead.
/// </summary>
internal static class TemplateVersionIntegrityTriggerCatalog
{
    public const string DropPublishedVersionContentImmutableStatement =
        "DROP TRIGGER IF EXISTS trg_published_template_version_content_immutable;";

    public static readonly string[] DropStatements =
    [
        "DROP TRIGGER IF EXISTS trg_test_session_requires_published_template_insert;",
        "DROP TRIGGER IF EXISTS trg_test_session_requires_published_template_update;",
        "DROP TRIGGER IF EXISTS trg_published_template_version_content_immutable;",
        "DROP TRIGGER IF EXISTS trg_published_template_version_no_delete;",
        "DROP TRIGGER IF EXISTS trg_active_version_belongs_to_template_insert;",
        "DROP TRIGGER IF EXISTS trg_active_version_belongs_to_template_update;",
        "DROP TRIGGER IF EXISTS trg_published_template_source_no_insert;",
        "DROP TRIGGER IF EXISTS trg_published_template_source_no_update;",
        "DROP TRIGGER IF EXISTS trg_published_template_source_no_delete;",
        "DROP TRIGGER IF EXISTS trg_published_question_no_insert;",
        "DROP TRIGGER IF EXISTS trg_published_question_no_update;",
        "DROP TRIGGER IF EXISTS trg_published_question_no_delete;",
        "DROP TRIGGER IF EXISTS trg_published_region_no_insert;",
        "DROP TRIGGER IF EXISTS trg_published_region_no_update;",
        "DROP TRIGGER IF EXISTS trg_published_region_no_delete;",
        "DROP TRIGGER IF EXISTS trg_published_answer_no_insert;",
        "DROP TRIGGER IF EXISTS trg_published_answer_no_update;",
        "DROP TRIGGER IF EXISTS trg_published_answer_no_delete;",
    ];

    public static readonly string[] Schema17Statements =
        CreateStatements(
            includeGenerationColumns: true,
            includeExpectedSubmissionPageCount: false);

    public static readonly string[] Schema16Statements =
        CreateStatements(
            includeGenerationColumns: false,
            includeExpectedSubmissionPageCount: false);

    public static readonly string[] Schema18Statements =
        CreateStatements(
            includeGenerationColumns: true,
            includeExpectedSubmissionPageCount: true);

    public static string Schema18PublishedVersionContentImmutableStatement =>
        Schema18Statements[2];

    public static string Schema17PublishedVersionContentImmutableStatement =>
        Schema17Statements[2];

    public static string Schema16PublishedVersionContentImmutableStatement =>
        Schema16Statements[2];

    private static string[] CreateStatements(
        bool includeGenerationColumns,
        bool includeExpectedSubmissionPageCount) =>
    [
        """
        CREATE TRIGGER IF NOT EXISTS trg_test_session_requires_published_template_insert
        BEFORE INSERT ON test_session
        WHEN (SELECT state FROM template_version WHERE id = NEW.template_version_id) <> 'published'
        BEGIN
            SELECT RAISE(ABORT, 'test_session_requires_published_template');
        END;
        """,
        """
        CREATE TRIGGER IF NOT EXISTS trg_test_session_requires_published_template_update
        BEFORE UPDATE OF template_version_id ON test_session
        WHEN (SELECT state FROM template_version WHERE id = NEW.template_version_id) <> 'published'
        BEGIN
            SELECT RAISE(ABORT, 'test_session_requires_published_template');
        END;
        """,
        CreatePublishedVersionImmutableTrigger(
            includeGenerationColumns,
            includeExpectedSubmissionPageCount),
        """
        CREATE TRIGGER IF NOT EXISTS trg_published_template_version_no_delete
        BEFORE DELETE ON template_version
        WHEN OLD.state IN ('published','superseded','retired')
        BEGIN
            SELECT RAISE(ABORT, 'published_template_version_requires_controlled_erasure');
        END;
        """,
        """
        CREATE TRIGGER IF NOT EXISTS trg_active_version_belongs_to_template_insert
        BEFORE INSERT ON test_template
        WHEN NEW.active_version_id IS NOT NULL
          AND NOT EXISTS (
            SELECT 1 FROM template_version
            WHERE id = NEW.active_version_id
              AND test_template_id = NEW.id
              AND state = 'published'
          )
        BEGIN
            SELECT RAISE(ABORT, 'active_version_must_be_a_published_version_of_template');
        END;
        """,
        """
        CREATE TRIGGER IF NOT EXISTS trg_active_version_belongs_to_template_update
        BEFORE UPDATE OF active_version_id ON test_template
        WHEN NEW.active_version_id IS NOT NULL
          AND NOT EXISTS (
            SELECT 1 FROM template_version
            WHERE id = NEW.active_version_id
              AND test_template_id = NEW.id
              AND state = 'published'
          )
        BEGIN
            SELECT RAISE(ABORT, 'active_version_must_be_a_published_version_of_template');
        END;
        """,
        """
        CREATE TRIGGER IF NOT EXISTS trg_published_template_source_no_insert
        BEFORE INSERT ON template_source
        WHEN EXISTS (
            SELECT 1 FROM template_version
            WHERE id = NEW.template_version_id
              AND state IN ('published','superseded','retired')
        )
        BEGIN
            SELECT RAISE(ABORT, 'published_template_content_is_immutable');
        END;
        """,
        """
        CREATE TRIGGER IF NOT EXISTS trg_published_template_source_no_update
        BEFORE UPDATE ON template_source
        WHEN EXISTS (
            SELECT 1 FROM template_version
            WHERE id IN (OLD.template_version_id, NEW.template_version_id)
              AND state IN ('published','superseded','retired')
        )
        BEGIN
            SELECT RAISE(ABORT, 'published_template_content_is_immutable');
        END;
        """,
        """
        CREATE TRIGGER IF NOT EXISTS trg_published_template_source_no_delete
        BEFORE DELETE ON template_source
        WHEN EXISTS (
            SELECT 1 FROM template_version
            WHERE id = OLD.template_version_id
              AND state IN ('published','superseded','retired')
        )
        BEGIN
            SELECT RAISE(ABORT, 'published_template_content_is_immutable');
        END;
        """,
        """
        CREATE TRIGGER IF NOT EXISTS trg_published_question_no_insert
        BEFORE INSERT ON question
        WHEN EXISTS (
            SELECT 1 FROM template_version
            WHERE id = NEW.template_version_id
              AND state IN ('published','superseded','retired')
        )
        BEGIN
            SELECT RAISE(ABORT, 'published_template_content_is_immutable');
        END;
        """,
        """
        CREATE TRIGGER IF NOT EXISTS trg_published_question_no_update
        BEFORE UPDATE ON question
        WHEN EXISTS (
            SELECT 1 FROM template_version
            WHERE id IN (OLD.template_version_id, NEW.template_version_id)
              AND state IN ('published','superseded','retired')
        )
        BEGIN
            SELECT RAISE(ABORT, 'published_template_content_is_immutable');
        END;
        """,
        """
        CREATE TRIGGER IF NOT EXISTS trg_published_question_no_delete
        BEFORE DELETE ON question
        WHEN EXISTS (
            SELECT 1 FROM template_version
            WHERE id = OLD.template_version_id
              AND state IN ('published','superseded','retired')
        )
        BEGIN
            SELECT RAISE(ABORT, 'published_template_content_is_immutable');
        END;
        """,
        """
        CREATE TRIGGER IF NOT EXISTS trg_published_region_no_insert
        BEFORE INSERT ON region
        WHEN EXISTS (
            SELECT 1 FROM question q
            JOIN template_version v ON v.id = q.template_version_id
            WHERE q.id = NEW.owner_id
              AND v.state IN ('published','superseded','retired')
        )
        BEGIN
            SELECT RAISE(ABORT, 'published_template_content_is_immutable');
        END;
        """,
        """
        CREATE TRIGGER IF NOT EXISTS trg_published_region_no_update
        BEFORE UPDATE ON region
        WHEN EXISTS (
            SELECT 1 FROM question q
            JOIN template_version v ON v.id = q.template_version_id
            WHERE q.id = OLD.owner_id
              AND v.state IN ('published','superseded','retired')
        )
        BEGIN
            SELECT RAISE(ABORT, 'published_template_content_is_immutable');
        END;
        """,
        """
        CREATE TRIGGER IF NOT EXISTS trg_published_region_no_delete
        BEFORE DELETE ON region
        WHEN EXISTS (
            SELECT 1 FROM question q
            JOIN template_version v ON v.id = q.template_version_id
            WHERE q.id = OLD.owner_id
              AND v.state IN ('published','superseded','retired')
        )
        BEGIN
            SELECT RAISE(ABORT, 'published_template_content_is_immutable');
        END;
        """,
        """
        CREATE TRIGGER IF NOT EXISTS trg_published_answer_no_insert
        BEFORE INSERT ON accepted_answer
        WHEN EXISTS (
            SELECT 1 FROM question q
            JOIN template_version v ON v.id = q.template_version_id
            WHERE q.id = NEW.question_id
              AND v.state IN ('published','superseded','retired')
        )
        BEGIN
            SELECT RAISE(ABORT, 'published_template_content_is_immutable');
        END;
        """,
        """
        CREATE TRIGGER IF NOT EXISTS trg_published_answer_no_update
        BEFORE UPDATE ON accepted_answer
        WHEN EXISTS (
            SELECT 1 FROM question q
            JOIN template_version v ON v.id = q.template_version_id
            WHERE q.id IN (OLD.question_id, NEW.question_id)
              AND v.state IN ('published','superseded','retired')
        )
        BEGIN
            SELECT RAISE(ABORT, 'published_template_content_is_immutable');
        END;
        """,
        """
        CREATE TRIGGER IF NOT EXISTS trg_published_answer_no_delete
        BEFORE DELETE ON accepted_answer
        WHEN EXISTS (
            SELECT 1 FROM question q
            JOIN template_version v ON v.id = q.template_version_id
            WHERE q.id = OLD.question_id
              AND v.state IN ('published','superseded','retired')
        )
        BEGIN
            SELECT RAISE(ABORT, 'published_template_content_is_immutable');
        END;
        """,
    ];

    private static string CreatePublishedVersionImmutableTrigger(
        bool includeGenerationColumns,
        bool includeExpectedSubmissionPageCount)
    {
        var generationColumns = includeGenerationColumns
            ? """
            OR NEW.test_type IS NOT OLD.test_type
            OR NEW.answer_style IS NOT OLD.answer_style
            OR NEW.prompt_system IS NOT OLD.prompt_system
            OR NEW.originating_batch_id IS NOT OLD.originating_batch_id
            OR NEW.originating_unit_id IS NOT OLD.originating_unit_id
            OR NEW.generation_profile_version IS NOT OLD.generation_profile_version
            OR NEW.generation_profile_json IS NOT OLD.generation_profile_json
            OR NEW.generation_profile_hash IS NOT OLD.generation_profile_hash
            OR NEW.step_set_index IS NOT OLD.step_set_index
            OR NEW.step_variation_index IS NOT OLD.step_variation_index
            OR NEW.printed_test_name IS NOT OLD.printed_test_name
            OR NEW.resolved_grade IS NOT OLD.resolved_grade
            """
            : string.Empty;
        var expectedSubmissionPageCountColumn = includeExpectedSubmissionPageCount
            ? "OR NEW.expected_submission_page_count IS NOT " +
                "OLD.expected_submission_page_count"
            : string.Empty;
        return $$"""
        CREATE TRIGGER IF NOT EXISTS trg_published_template_version_content_immutable
        BEFORE UPDATE ON template_version
        WHEN OLD.state IN ('published','superseded','retired')
          AND (
            NEW.test_template_id IS NOT OLD.test_template_id
            OR NEW.version_number IS NOT OLD.version_number
            OR NEW.based_on_version_id IS NOT OLD.based_on_version_id
            OR NEW.target_total_points_milli IS NOT OLD.target_total_points_milli
            OR NEW.default_points_milli IS NOT OLD.default_points_milli
            OR NEW.default_allow_non_kanji IS NOT OLD.default_allow_non_kanji
            OR NEW.pipeline_version IS NOT OLD.pipeline_version
            OR NEW.ai_generation_provenance_id IS NOT OLD.ai_generation_provenance_id
            OR NEW.published_by_staff_user_id IS NOT OLD.published_by_staff_user_id
            OR NEW.published_at IS NOT OLD.published_at
            OR NEW.content_hash IS NOT OLD.content_hash
            {{generationColumns}}
            {{expectedSubmissionPageCountColumn}}
          )
        BEGIN
            SELECT RAISE(ABORT, 'published_template_version_content_is_immutable');
        END;
        """;
    }
}
