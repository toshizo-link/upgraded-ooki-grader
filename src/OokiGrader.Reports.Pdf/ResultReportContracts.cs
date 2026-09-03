namespace OokiGrader.Reports.Pdf;

public sealed record ResultReportDocument(
    string ReportId,
    string SchoolName,
    string StudentDisplayName,
    string? StudentNumber,
    string TestTitle,
    DateOnly TestDate,
    int TemplateVersionNumber,
    long ResultRevision,
    long EarnedPointsMilli,
    long PossiblePointsMilli,
    IReadOnlyList<ResultReportQuestion> Questions,
    DateTimeOffset GeneratedAt,
    bool IsCorrectedGrade,
    bool IncludeTeacherComments = true,
    string? StudentGradeLabel = null,
    string? StudentClassLabel = null,
    string? OriginalScanSha256 = null);

public sealed record ResultReportQuestion(
    string DisplayLabel,
    string QuestionText,
    string? RecognizedAnswer,
    long AwardedPointsMilli,
    long MaximumPointsMilli,
    string Outcome,
    bool IsCorrected,
    string? TeacherComment,
    IReadOnlyList<string>? ModelAnswers = null,
    string? MajorQuestionLabel = null,
    string? MiddleQuestionLabel = null,
    string? MinorQuestionLabel = null);

public sealed record ResultPdfRenderResult(
    byte[] PdfBytes,
    string Sha256,
    int PageCount,
    string RendererVersion);

public interface IResultPdfRenderer
{
    ResultPdfRenderResult Render(ResultReportDocument report);
}
