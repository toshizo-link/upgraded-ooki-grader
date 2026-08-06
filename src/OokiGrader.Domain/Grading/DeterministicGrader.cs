using OokiGrader.Domain.Scoring;
using OokiGrader.Domain.Templates;

namespace OokiGrader.Domain.Grading;

public static class DeterministicGrader
{
    public static QuestionGradeResult Grade(
        QuestionDefinition question,
        AnswerObservation observation)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(observation);

        var normalized = JapaneseTextNormalizer.NormalizeForComparison(
            observation.Transcription);

        if (observation.Quality != AnswerQuality.Clear)
        {
            return Result(
                question,
                MilliPoints.Zero,
                GradeDisposition.ReviewRequired,
                GradingStage.Quality,
                GradeReason.UnreadableOrAmbiguous,
                requiresReview: true,
                normalized);
        }

        if (observation.ExplicitlyBlank || normalized.Length == 0)
        {
            return Result(
                question,
                MilliPoints.Zero,
                GradeDisposition.Blank,
                GradingStage.Blank,
                GradeReason.BlankResponse,
                requiresReview: question.RequiresReviewAlways,
                normalized);
        }

        var canonical = question.CanonicalAnswer;
        if (question.QuestionType is not (
                QuestionType.Numeric
                or QuestionType.MultipleChoice
                or QuestionType.Boolean)
            && canonical is not null
            && JapaneseTextNormalizer.ExactEquals(
                observation.Transcription,
                canonical.AnswerText))
        {
            return FullCredit(
                question,
                GradingStage.Exact,
                GradeReason.ExactCanonicalMatch,
                normalized);
        }

        if (question.QuestionType == QuestionType.Numeric)
        {
            return GradeNumeric(question, observation, normalized);
        }

        if (question.QuestionType is QuestionType.MultipleChoice or QuestionType.Boolean)
        {
            return GradeChoice(question, normalized);
        }

        if (!question.AllowNonKanji
            && canonical is not null
            && KanjiDetector.ContainsKanji(canonical.AnswerText))
        {
            var phoneticException = question.AcceptedAnswers.FirstOrDefault(
                answer => answer.VariantType == AcceptedAnswerVariantType.PhoneticException
                    && string.Equals(
                        answer.NormalizedText,
                        normalized,
                        StringComparison.Ordinal));

            if (phoneticException is not null)
            {
                return FullCredit(
                    question,
                    GradingStage.KanjiPolicy,
                    GradeReason.PhoneticException,
                    normalized);
            }

            if (observation.ScriptObservationUncertain)
            {
                return Result(
                    question,
                    MilliPoints.Zero,
                    GradeDisposition.ReviewRequired,
                    GradingStage.KanjiPolicy,
                    GradeReason.KanjiObservationUncertain,
                    requiresReview: true,
                    normalized);
            }

            if (!KanjiDetector.ContainsKanji(observation.Transcription))
            {
                return Result(
                    question,
                    MilliPoints.Zero,
                    GradeDisposition.Incorrect,
                    GradingStage.KanjiPolicy,
                    GradeReason.RequiredKanjiAbsent,
                    requiresReview: question.RequiresReviewAlways,
                    normalized);
            }
        }

        var matchingVariant = question.AcceptedAnswers.FirstOrDefault(
            answer => answer.VariantType is AcceptedAnswerVariantType.Canonical
                or AcceptedAnswerVariantType.Equivalent
                or AcceptedAnswerVariantType.PhoneticException
                && string.Equals(answer.NormalizedText, normalized, StringComparison.Ordinal));

        if (matchingVariant is not null)
        {
            return FullCredit(
                question,
                GradingStage.ExplicitVariant,
                matchingVariant.VariantType == AcceptedAnswerVariantType.PhoneticException
                    ? GradeReason.PhoneticException
                    : GradeReason.ExplicitVariantMatch,
                normalized);
        }

        if (question.RubricRules.Count > 0)
        {
            return GradeRubric(question, observation, normalized);
        }

        if (question.RequiresReviewAlways
            || question.GradingMode is GradingMode.AiRubric or GradingMode.Manual
            || question.QuestionType is QuestionType.SemanticShortText
                or QuestionType.Subjective
                or QuestionType.Unsupported)
        {
            return Result(
                question,
                MilliPoints.Zero,
                GradeDisposition.ReviewRequired,
                GradingStage.Review,
                question.RequiresReviewAlways
                    ? GradeReason.AlwaysReview
                    : GradeReason.ManualOrSubjective,
                requiresReview: true,
                normalized);
        }

        return Result(
            question,
            MilliPoints.Zero,
            GradeDisposition.Incorrect,
            GradingStage.ExplicitVariant,
            GradeReason.NoAcceptedMatch,
            requiresReview: false,
            normalized);
    }

    private static QuestionGradeResult GradeNumeric(
        QuestionDefinition question,
        AnswerObservation observation,
        string normalized)
    {
        if (question.NumericPolicy is null)
        {
            return Result(
                question,
                MilliPoints.Zero,
                GradeDisposition.ReviewRequired,
                GradingStage.Numeric,
                GradeReason.NumericUnparseable,
                requiresReview: true,
                normalized);
        }

        var parsed = NumericAnswerParser.Parse(observation.Transcription, question.NumericPolicy);
        if (!parsed.Success)
        {
            return Result(
                question,
                MilliPoints.Zero,
                GradeDisposition.ReviewRequired,
                GradingStage.Numeric,
                parsed.Failure == NumericParseFailure.UnitMissingOrInvalid
                    ? GradeReason.UnitMissingOrInvalid
                    : GradeReason.NumericUnparseable,
                requiresReview: true,
                normalized);
        }

        return NumericAnswerParser.Matches(parsed.Value, question.NumericPolicy)
            ? FullCredit(
                question,
                GradingStage.Numeric,
                GradeReason.NumericMatch,
                normalized)
            : Result(
                question,
                MilliPoints.Zero,
                GradeDisposition.Incorrect,
                GradingStage.Numeric,
                GradeReason.NumericMismatch,
                requiresReview: question.RequiresReviewAlways,
                normalized);
    }

    private static QuestionGradeResult GradeChoice(
        QuestionDefinition question,
        string normalized)
    {
        if (question.ChoicePolicy is null)
        {
            return Result(
                question,
                MilliPoints.Zero,
                GradeDisposition.ReviewRequired,
                GradingStage.Choice,
                GradeReason.ChoiceUnrecognized,
                requiresReview: true,
                normalized);
        }

        if (!ChoiceLabelNormalizer.TryMatchAllowedChoice(
                normalized,
                question.ChoicePolicy.AllowedChoices,
                out var matchedChoice))
        {
            return Result(
                question,
                MilliPoints.Zero,
                GradeDisposition.ReviewRequired,
                GradingStage.Choice,
                GradeReason.ChoiceUnrecognized,
                requiresReview: true,
                normalized);
        }

        if (string.Equals(
                matchedChoice,
                question.ChoicePolicy.CorrectChoice,
                StringComparison.Ordinal))
        {
            return FullCredit(
                question,
                GradingStage.Choice,
                GradeReason.ChoiceMatch,
                matchedChoice);
        }

        return Result(
            question,
            MilliPoints.Zero,
            GradeDisposition.Incorrect,
            GradingStage.Choice,
            GradeReason.ChoiceMismatch,
            requiresReview: question.RequiresReviewAlways,
            matchedChoice);
    }

    private static QuestionGradeResult GradeRubric(
        QuestionDefinition question,
        AnswerObservation observation,
        string normalized)
    {
        var assessments = observation.RubricAssessments;
        var duplicateAssessment = assessments
            .GroupBy(assessment => assessment.RuleId, StringComparer.Ordinal)
            .Any(group => group.Count() > 1);
        var knownRuleIds = question.RubricRules
            .Select(rule => rule.Id)
            .ToHashSet(StringComparer.Ordinal);
        var assessmentIds = assessments
            .Select(assessment => assessment.RuleId)
            .ToHashSet(StringComparer.Ordinal);

        if (duplicateAssessment
            || !knownRuleIds.SetEquals(assessmentIds))
        {
            return Result(
                question,
                MilliPoints.Zero,
                GradeDisposition.ReviewRequired,
                GradingStage.Rubric,
                GradeReason.RubricAssessmentInvalid,
                requiresReview: true,
                normalized);
        }

        var satisfiedIds = assessments
            .Where(assessment => assessment.Satisfied)
            .Select(assessment => assessment.RuleId)
            .ToHashSet(StringComparer.Ordinal);
        var satisfiedRules = question.RubricRules
            .Where(rule => satisfiedIds.Contains(rule.Id))
            .ToArray();

        var violatesExclusivity = satisfiedRules
            .Where(rule => rule.MutuallyExclusiveGroup is not null)
            .GroupBy(rule => rule.MutuallyExclusiveGroup, StringComparer.Ordinal)
            .Any(group => group.Count() > 1);
        if (violatesExclusivity)
        {
            return Result(
                question,
                MilliPoints.Zero,
                GradeDisposition.ReviewRequired,
                GradingStage.Rubric,
                GradeReason.RubricAssessmentInvalid,
                requiresReview: true,
                normalized);
        }

        var proposed = satisfiedRules.Aggregate(
            MilliPoints.Zero,
            (sum, rule) => sum + rule.Points);
        if (proposed > question.MaximumPoints
            || !question.PointPolicy.ValidateAward(proposed).IsValid)
        {
            return Result(
                question,
                MilliPoints.Zero,
                GradeDisposition.ReviewRequired,
                GradingStage.Rubric,
                GradeReason.RubricAssessmentInvalid,
                requiresReview: true,
                normalized);
        }

        var disposition = proposed == MilliPoints.Zero
            ? GradeDisposition.Incorrect
            : proposed == question.MaximumPoints
                ? GradeDisposition.Correct
                : GradeDisposition.Partial;

        return Result(
            question,
            proposed,
            disposition,
            GradingStage.Rubric,
            GradeReason.RubricProposal,
            requiresReview: true,
            normalized);
    }

    private static QuestionGradeResult FullCredit(
        QuestionDefinition question,
        GradingStage stage,
        GradeReason reason,
        string normalized) =>
        Result(
            question,
            question.MaximumPoints,
            GradeDisposition.Correct,
            stage,
            reason,
            question.RequiresReviewAlways,
            normalized);

    private static QuestionGradeResult Result(
        QuestionDefinition question,
        MilliPoints points,
        GradeDisposition disposition,
        GradingStage stage,
        GradeReason reason,
        bool requiresReview,
        string normalized)
    {
        question.PointPolicy.ValidateAward(points, question.Id).ThrowIfInvalid();
        return new QuestionGradeResult(
            question.Id,
            points,
            question.MaximumPoints,
            disposition,
            stage,
            reason,
            requiresReview,
            normalized);
    }
}
