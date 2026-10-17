using OokiGrader.Domain.Grading;

namespace OokiGrader.Host.Jobs;

internal sealed record CanonicalTemplateGenerationDraft(
    string SchemaVersion,
    CanonicalTemplateGenerationMetadata Metadata,
    IReadOnlyList<CanonicalTemplateGenerationPage> Pages,
    IReadOnlyList<TemplateExtractionReviewIssue> ReviewIssues,
    long TotalPointsMilli);

internal sealed record CanonicalTemplateGenerationMetadata(
    string? PrintedTestName,
    string? PrintedGradeLabel,
    double GradeConfidence,
    IReadOnlyList<string> Warnings);

internal sealed record CanonicalTemplateGenerationPage(
    string SourceId,
    int PageNumber,
    int DetectedAnswerSlotCount,
    IReadOnlyList<CanonicalTemplateGenerationQuestion> Questions);

internal sealed record CanonicalTemplateGenerationQuestion(
    string SourceKey,
    string DisplayLabel,
    string QuestionText,
    int AnswerSlotOrdinal,
    int AnswerSlotCount,
    bool FilledAnswerRemoved,
    bool IsEmbeddedFillBlank,
    string QuestionType,
    string? ExpectedAnswer,
    string AnswerProvenance,
    TemplateExtractionAnswerSource? AnswerSource,
    IReadOnlyList<string> AcceptedVariants,
    long SuggestedPointsMilli,
    bool AllowNonKanjiSuggestion,
    bool RequiresCompleteAnswerSuggestion,
    bool AnswerOrderInsensitiveSuggestion,
    bool RequiresTeacherAnswer,
    double Confidence,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<TemplateExtractionReviewIssue> ReviewIssues,
    string? MajorQuestionLabel = null,
    string? MiddleQuestionLabel = null,
    string? MinorQuestionLabel = null);

internal static class TemplateGenerationDraftFactory
{
    public static CanonicalTemplateGenerationDraft Normalize(CanonicalTemplateGenerationDraft draft)
    {
        if (draft.Pages is null || draft.Pages.Any(page => page is null)) return draft;
        return draft with
        {
            Pages = draft.Pages.OrderBy(page => page.PageNumber).Select(page => page with
            {
                Questions = page.Questions is null ? null! : page.Questions.Select(question =>
                {
                    if (question is null || question.AcceptedVariants is null) return question!;
                    var expected = question.ExpectedAnswer is null ? null
                        : JapaneseTextNormalizer.NormalizeForComparison(question.ExpectedAnswer);
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    return question with
                    {
                        AcceptedVariants = question.AcceptedVariants.Where(variant =>
                        {
                            // Invalid empty answers remain invalid at validation.
                            if (string.IsNullOrWhiteSpace(variant)) return true;
                            var normalized = JapaneseTextNormalizer.NormalizeForComparison(variant);
                            return normalized != expected && seen.Add(normalized);
                        }).ToArray(),
                    };
                }).ToArray(),
            }).ToArray(),
        };
    }

    public static CanonicalTemplateGenerationDraft Create(
        ValidatedTemplateExtraction extraction)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        return new CanonicalTemplateGenerationDraft(
            "template_extract_v6",
            new CanonicalTemplateGenerationMetadata(
                extraction.Metadata.Title,
                extraction.Metadata.GradeLabel,
                extraction.Metadata.Confidence,
                extraction.Metadata.Warnings),
            extraction.Pages.Select(page => new CanonicalTemplateGenerationPage(
                page.SourceId,
                page.PageNumber,
                page.DetectedAnswerSlotCount,
                page.Questions.Select(question =>
                    new CanonicalTemplateGenerationQuestion(
                        question.SourceKey,
                        question.DisplayLabel,
                        question.QuestionText,
                        question.AnswerSlotOrdinal,
                        question.AnswerSlotCount,
                        question.FilledAnswerRemoved,
                        question.IsEmbeddedFillBlank,
                        question.QuestionType,
                        question.ExpectedAnswer,
                        question.AnswerProvenance,
                        question.AnswerSource,
                        question.AcceptedVariants,
                        question.SuggestedPointsMilli,
                        question.AllowNonKanjiSuggestion,
                        question.RequiresCompleteAnswerSuggestion,
                        question.AnswerOrderInsensitiveSuggestion,
                        question.RequiresTeacherAnswer,
                        question.Confidence,
                        question.Warnings,
                        question.ReviewIssues,
                        question.MajorQuestionLabel,
                        question.MiddleQuestionLabel,
                        question.MinorQuestionLabel))
                    .ToArray()))
                .ToArray(),
            extraction.ReviewIssues,
            extraction.TotalPointsMilli);
    }
}
