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
    bool IncludeTeacherComments = true);

public sealed record ResultReportQuestion(
    string DisplayLabel,
    string QuestionText,
    string? RecognizedAnswer,
    long AwardedPointsMilli,
    long MaximumPointsMilli,
    string Outcome,
    bool IsCorrected,
    string? TeacherComment);

public sealed record ResultPdfRenderResult(
    byte[] PdfBytes,
    string Sha256,
    int PageCount,
    string RendererVersion);

public interface IResultPdfRenderer
{
    ResultPdfRenderResult Render(ResultReportDocument report);
}
