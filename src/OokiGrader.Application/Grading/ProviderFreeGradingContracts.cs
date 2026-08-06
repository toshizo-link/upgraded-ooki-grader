namespace OokiGrader.Application.Grading;

public sealed record QuestionDefinition(
    string QuestionId,
    long MaxPointsMilli);

public sealed record QuestionJudgment(
    string QuestionId,
    long AwardedPointsMilli,
    string Outcome,
    string Method,
    int ConfidenceBasisPoints,
    string? TranscribedAnswer = null,
    string? NormalizedAnswer = null,
    string? ReasonCode = null);

public sealed record ProviderFreeGradingRunDraft(
    string GradingRunId,
    string SubmissionId,
    string TemplateVersionId,
    int RunNumber,
    string Reason,
    string CanonicalInputManifestHash,
    IReadOnlyList<QuestionDefinition> Questions,
    IReadOnlyList<QuestionJudgment> Judgments);

public sealed record ProviderFreeGradingRunSnapshot(
    string GradingRunId,
    string SubmissionId,
    string TemplateVersionId,
    int RunNumber,
    string State,
    long EarnedPointsMilli,
    long PossiblePointsMilli,
    IReadOnlyList<QuestionJudgment> Judgments);

public sealed record ValidatedGradeTotals(
    long EarnedPointsMilli,
    long PossiblePointsMilli);

public interface IProviderFreeGradingStore
{
    Task<ProviderFreeGradingRunSnapshot> CreateAsync(
        ProviderFreeGradingRunDraft draft,
        CancellationToken cancellationToken = default);

    Task<ProviderFreeGradingRunSnapshot?> GetAsync(
        string gradingRunId,
        CancellationToken cancellationToken = default);
}
