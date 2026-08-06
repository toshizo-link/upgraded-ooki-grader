using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Abstractions;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Infrastructure.Persistence;

public sealed record OokiDatabaseInitializationOptions(
    string DataRoot,
    string SchoolName = "",
    string TimeZone = "Asia/Tokyo",
    string Locale = "ja-JP",
    string? BootstrapTokenHash = null,
    DateTimeOffset? BootstrapTokenExpiresAt = null);

public sealed class OokiDatabaseInitializer(
    OokiGraderDbContext dbContext,
    IClock clock)
{
    public async Task InitializeAsync(
        OokiDatabaseInitializationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!Path.IsPathFullyQualified(options.DataRoot))
        {
            throw new ArgumentException(
                "The data root must be an absolute path.",
                nameof(options));
        }

        await dbContext.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "PRAGMA journal_mode=WAL;",
                cancellationToken).ConfigureAwait(false);
            await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            await InstallIntegrityTriggersAsync(cancellationToken).ConfigureAwait(false);

            if (!await dbContext.SiteSettings.AnyAsync(cancellationToken).ConfigureAwait(false))
            {
                var now = clock.UtcNow;
                dbContext.SiteSettings.Add(new SiteSettingsEntity
                {
                    Id = "site",
                    SchoolName = options.SchoolName,
                    TimeZone = options.TimeZone,
                    Locale = options.Locale,
                    DataRoot = Path.GetFullPath(options.DataRoot),
                    BootstrapTokenHash = options.BootstrapTokenHash,
                    BootstrapTokenExpiresAt = options.BootstrapTokenExpiresAt,
                    CreatedAt = now,
                    UpdatedAt = now,
                    Revision = 1
                });
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private async Task InstallIntegrityTriggersAsync(CancellationToken cancellationToken)
    {
        foreach (var statement in IntegrityTriggerStatements)
        {
            await dbContext.Database.ExecuteSqlRawAsync(statement, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static readonly string[] IntegrityTriggerStatements =
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
        """
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
          )
        BEGIN
            SELECT RAISE(ABORT, 'published_template_version_content_is_immutable');
        END;
        """,
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
            SELECT 1
            FROM question q
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
            SELECT 1
            FROM question q
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
            SELECT 1
            FROM question q
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
            SELECT 1
            FROM question q
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
            SELECT 1
            FROM question q
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
            SELECT 1
            FROM question q
            JOIN template_version v ON v.id = q.template_version_id
            WHERE q.id = OLD.question_id
              AND v.state IN ('published','superseded','retired')
        )
        BEGIN
            SELECT RAISE(ABORT, 'published_template_content_is_immutable');
        END;
        """,
        """
        CREATE TRIGGER IF NOT EXISTS trg_result_revision_maximum_insert
        BEFORE INSERT ON result_revision
        WHEN NEW.awarded_points_milli > (
            SELECT maximum_points_milli
            FROM question_result
            WHERE id = NEW.question_result_id
        )
        BEGIN
            SELECT RAISE(ABORT, 'result_revision_exceeds_maximum');
        END;
        """,
        """
        CREATE TRIGGER IF NOT EXISTS trg_question_result_exact_template_insert
        BEFORE INSERT ON question_result
        WHEN NOT EXISTS (
            SELECT 1
            FROM grading_run r
            JOIN question q ON q.id = NEW.question_id
            WHERE r.id = NEW.grading_run_id
              AND r.template_version_id = q.template_version_id
        )
        BEGIN
            SELECT RAISE(ABORT, 'grading_question_must_match_exact_template_version');
        END;
        """,
        """
        CREATE TRIGGER IF NOT EXISTS trg_grading_run_exact_template_insert
        BEFORE INSERT ON grading_run
        WHEN NOT EXISTS (
            SELECT 1
            FROM submission s
            JOIN test_session ts ON ts.id = s.test_session_id
            WHERE s.id = NEW.submission_id
              AND ts.template_version_id = NEW.template_version_id
        )
        BEGIN
            SELECT RAISE(ABORT, 'grading_run_must_match_submission_template_version');
        END;
        """,
        """
        CREATE TRIGGER IF NOT EXISTS trg_submission_current_run_update
        BEFORE UPDATE OF current_grading_run_id ON submission
        WHEN NEW.current_grading_run_id IS NOT NULL
          AND NOT EXISTS (
            SELECT 1 FROM grading_run
            WHERE id = NEW.current_grading_run_id
              AND submission_id = NEW.id
          )
        BEGIN
            SELECT RAISE(ABORT, 'current_grading_run_must_belong_to_submission');
        END;
        """,
        """
        CREATE TRIGGER IF NOT EXISTS trg_question_result_current_revision_update
        BEFORE UPDATE OF current_revision_id ON question_result
        WHEN NEW.current_revision_id IS NOT NULL
          AND NOT EXISTS (
            SELECT 1 FROM result_revision
            WHERE id = NEW.current_revision_id
              AND question_result_id = NEW.id
          )
        BEGIN
            SELECT RAISE(ABORT, 'current_revision_must_belong_to_question_result');
        END;
        """,
        """
        CREATE TRIGGER IF NOT EXISTS trg_result_revision_append_only_update
        BEFORE UPDATE ON result_revision
        BEGIN
            SELECT RAISE(ABORT, 'result_revision_is_append_only');
        END;
        """,
        """
        CREATE TRIGGER IF NOT EXISTS trg_result_revision_append_only_delete
        BEFORE DELETE ON result_revision
        BEGIN
            SELECT RAISE(ABORT, 'result_revision_is_append_only');
        END;
        """,
        """
        CREATE TRIGGER IF NOT EXISTS trg_audit_event_append_only_update
        BEFORE UPDATE ON audit_event
        BEGIN
            SELECT RAISE(ABORT, 'audit_event_is_append_only');
        END;
        """,
        """
        CREATE TRIGGER IF NOT EXISTS trg_audit_event_append_only_delete
        BEFORE DELETE ON audit_event
        BEGIN
            SELECT RAISE(ABORT, 'audit_event_is_append_only');
        END;
        """
    ];
}
