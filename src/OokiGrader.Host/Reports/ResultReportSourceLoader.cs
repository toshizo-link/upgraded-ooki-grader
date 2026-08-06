using Microsoft.EntityFrameworkCore;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Reports.Pdf;

namespace OokiGrader.Host.Reports;

internal static class ResultReportSourceLoader
{
    public static async Task<ResultReportSource> LoadAsync(
        OokiGraderDbContext db,
        string submissionId,
        string reportId,
        DateTimeOffset generatedAt,
        bool includeTeacherComments,
        CancellationToken cancellationToken)
    {
        var submission = await db.Submissions
            .AsNoTracking()
            .Include(item => item.AssignedStudent)
            .Include(item => item.TestSession)
                .ThenInclude(item => item.TemplateVersion)
                    .ThenInclude(item => item.TestTemplate)
            .Include(item => item.GradingRuns)
                .ThenInclude(item => item.QuestionResults)
                    .ThenInclude(item => item.Question)
            .Include(item => item.GradingRuns)
                .ThenInclude(item => item.QuestionResults)
                    .ThenInclude(item => item.Revisions)
            .SingleOrDefaultAsync(
                item => item.Id == submissionId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ResultReportSourceException(
                "export_submission_missing",
                "The finalized result no longer exists.");

        if (submission.FinalizedAt is null
            || submission.VoidedAt is not null
            || submission.State != "finalized"
            || submission.CurrentGradingRunId is null)
        {
            throw new ResultReportSourceException(
                "export_result_not_finalized",
                "Only a currently finalized result can be exported.");
        }

        if (submission.AssignedStudent is null)
        {
            throw new ResultReportSourceException(
                "export_student_missing",
                "A per-student report requires an assigned student.");
        }

        var run = submission.GradingRuns.SingleOrDefault(
                item => item.Id == submission.CurrentGradingRunId)
            ?? throw new ResultReportSourceException(
                "export_grading_run_missing",
                "The current grading run is unavailable.");
        var version = submission.TestSession.TemplateVersion;
        if (run.State != "finalized"
            || run.TemplateVersionId != version.Id
            || version.VersionNumber <= 0)
        {
            throw new ResultReportSourceException(
                "export_result_source_invalid",
                "The finalized result does not match its template version.");
        }

        var settings = await db.SiteSettings
            .AsNoTracking()
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
        var questionRows = run.QuestionResults
            .OrderBy(item => item.Question.OrderIndex)
            .ThenBy(item => item.QuestionId, StringComparer.Ordinal)
            .Select(item =>
            {
                var current = item.Revisions.SingleOrDefault(
                        revision => revision.Id == item.CurrentRevisionId)
                    ?? throw new ResultReportSourceException(
                        "export_result_revision_missing",
                        "A current question result revision is unavailable.");
                return new ResultReportQuestion(
                    item.Question.DisplayLabel,
                    item.Question.QuestionText,
                    current.AnswerTextCorrection ?? item.TranscribedAnswer,
                    current.AwardedPointsMilli,
                    item.MaximumPointsMilli,
                    current.Outcome,
                    current.Source != "initial",
                    includeTeacherComments ? current.TeacherNote : null);
            })
            .ToArray();
        var earned = questionRows.Aggregate(
            0L,
            static (total, item) =>
                checked(total + item.AwardedPointsMilli));
        var possible = questionRows.Aggregate(
            0L,
            static (total, item) =>
                checked(total + item.MaximumPointsMilli));
        if (earned != run.EarnedPointsMilli
            || possible != run.PossiblePointsMilli)
        {
            throw new ResultReportSourceException(
                "export_result_total_invalid",
                "The finalized question totals are inconsistent.");
        }

        var document = new ResultReportDocument(
            reportId,
            settings.SchoolName,
            submission.AssignedStudent.DisplayName,
            submission.AssignedStudent.StudentNumber,
            submission.TestSession.TitleOverride
                ?? version.TestTemplate.Title,
            submission.TestSession.TestDate,
            version.VersionNumber,
            run.ResultSourceRevision,
            earned,
            possible,
            questionRows,
            generatedAt,
            questionRows.Any(item => item.IsCorrected),
            includeTeacherComments);
        return new ResultReportSource(
            document,
            submission.Id,
            submission.Revision,
            run.Id,
            run.ResultSourceRevision,
            version.Id,
            version.VersionNumber,
            ResultReportSourceHasher.Compute(document));
    }
}

internal sealed record ResultReportSource(
    ResultReportDocument Document,
    string SubmissionId,
    long SubmissionRevision,
    string GradingRunId,
    long ResultSourceRevision,
    string TemplateVersionId,
    int TemplateVersionNumber,
    string SourceHash);

internal sealed class ResultReportSourceException(
    string errorCode,
    string safeDetail) : Exception(safeDetail)
{
    public string ErrorCode { get; } = errorCode;
    public string SafeDetail { get; } = safeDetail;
}
