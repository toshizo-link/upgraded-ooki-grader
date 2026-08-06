using System.Collections.ObjectModel;
using OokiGrader.Domain.Common;
using OokiGrader.Domain.Scoring;

namespace OokiGrader.Domain.Grading;

public enum AnswerQuality
{
    Clear,
    Unreadable,
    Cropped,
    Ambiguous,
}

public enum GradeDisposition
{
    Correct,
    Incorrect,
    Partial,
    Blank,
    ReviewRequired,
}

public enum GradingStage
{
    Quality,
    Blank,
    Exact,
    Numeric,
    Choice,
    KanjiPolicy,
    ExplicitVariant,
    Rubric,
    Review,
}

public enum GradeReason
{
    ExactCanonicalMatch,
    NumericMatch,
    NumericMismatch,
    NumericUnparseable,
    UnitMissingOrInvalid,
    ChoiceMatch,
    ChoiceMismatch,
    ChoiceUnrecognized,
    RequiredKanjiAbsent,
    KanjiObservationUncertain,
    PhoneticException,
    ExplicitVariantMatch,
    BlankResponse,
    UnreadableOrAmbiguous,
    RubricProposal,
    RubricAssessmentInvalid,
    ManualOrSubjective,
    AlwaysReview,
    NoAcceptedMatch,
}

public sealed record RubricRuleAssessment(string RuleId, bool Satisfied);

public sealed class AnswerObservation
{
    private readonly ReadOnlyCollection<RubricRuleAssessment> _rubricAssessments;

    public AnswerObservation(
        string? transcription,
        AnswerQuality quality = AnswerQuality.Clear,
        bool explicitlyBlank = false,
        bool scriptObservationUncertain = false,
        IEnumerable<RubricRuleAssessment>? rubricAssessments = null)
    {
        Transcription = transcription ?? string.Empty;
        Quality = quality;
        ExplicitlyBlank = explicitlyBlank;
        ScriptObservationUncertain = scriptObservationUncertain;
        _rubricAssessments = Array.AsReadOnly((rubricAssessments ?? []).ToArray());
    }

    public string Transcription { get; }

    public AnswerQuality Quality { get; }

    public bool ExplicitlyBlank { get; }

    public bool ScriptObservationUncertain { get; }

    public IReadOnlyList<RubricRuleAssessment> RubricAssessments => _rubricAssessments;
}

public sealed class QuestionGradeResult
{
    public QuestionGradeResult(
        string questionId,
        MilliPoints awardedPoints,
        MilliPoints maximumPoints,
        GradeDisposition disposition,
        GradingStage stage,
        GradeReason reason,
        bool requiresReview,
        string normalizedTranscription)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(questionId);
        ArgumentNullException.ThrowIfNull(normalizedTranscription);
        if (awardedPoints > maximumPoints)
        {
            throw new DomainValidationException(
            [
                new DomainError(
                    "grade.points_above_maximum",
                    "A grade result cannot award more than the question maximum."),
            ]);
        }

        if (disposition == GradeDisposition.ReviewRequired && !requiresReview)
        {
            throw new DomainValidationException(
            [
                new DomainError(
                    "grade.review_flag_required",
                    "A review-required disposition must set the review flag."),
            ]);
        }

        QuestionId = questionId;
        AwardedPoints = awardedPoints;
        MaximumPoints = maximumPoints;
        Disposition = disposition;
        Stage = stage;
        Reason = reason;
        RequiresReview = requiresReview;
        NormalizedTranscription = normalizedTranscription;
    }

    public string QuestionId { get; }

    public MilliPoints AwardedPoints { get; }

    public MilliPoints MaximumPoints { get; }

    public GradeDisposition Disposition { get; }

    public GradingStage Stage { get; }

    public GradeReason Reason { get; }

    public bool RequiresReview { get; }

    public string NormalizedTranscription { get; }
}
