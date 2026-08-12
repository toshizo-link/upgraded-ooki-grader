using OokiGrader.Domain.Grading;
using OokiGrader.Domain.Scoring;
using OokiGrader.Domain.Templates;

namespace OokiGrader.Domain.Tests;

public sealed class DeterministicGraderTests
{
    [Fact]
    public void QualityGatePrecedesBlankDetection()
    {
        var result = DeterministicGrader.Grade(
            TestQuestionFactory.ExactText(),
            new AnswerObservation(
                "",
                AnswerQuality.Unreadable,
                explicitlyBlank: true));

        Assert.Equal(GradeDisposition.ReviewRequired, result.Disposition);
        Assert.Equal(GradingStage.Quality, result.Stage);
        Assert.Equal(GradeReason.UnreadableOrAmbiguous, result.Reason);
    }

    [Fact]
    public void ClearWhitespaceResponseIsBlank()
    {
        var result = DeterministicGrader.Grade(
            TestQuestionFactory.ExactText(),
            new AnswerObservation("　 "));

        Assert.Equal(GradeDisposition.Blank, result.Disposition);
        Assert.Equal(GradingStage.Blank, result.Stage);
        Assert.Equal(MilliPoints.Zero, result.AwardedPoints);
    }

    [Fact]
    public void ExactKanjiAnswerReceivesFullCredit()
    {
        var result = DeterministicGrader.Grade(
            TestQuestionFactory.ExactText(canonicalText: "漢字"),
            new AnswerObservation("漢字"));

        Assert.Equal(GradeDisposition.Correct, result.Disposition);
        Assert.Equal(GradingStage.Exact, result.Stage);
        Assert.Equal(1000, result.AwardedPoints.Value);
    }

    [Fact]
    public void KanaEquivalentDoesNotBypassRequiredKanji()
    {
        var kanaVariant = TestQuestionFactory.Answer(
            "kana-equivalent",
            "かんじ",
            AcceptedAnswerVariantType.Equivalent);
        var question = TestQuestionFactory.ExactText(
            canonicalText: "漢字",
            allowNonKanji: false,
            additionalAnswers: [kanaVariant]);

        var result = DeterministicGrader.Grade(
            question,
            new AnswerObservation("かんじ"));

        Assert.Equal(GradeDisposition.Incorrect, result.Disposition);
        Assert.Equal(GradingStage.KanjiPolicy, result.Stage);
        Assert.Equal(GradeReason.RequiredKanjiAbsent, result.Reason);
        Assert.Equal(MilliPoints.Zero, result.AwardedPoints);
    }

    [Fact]
    public void ExplicitPhoneticExceptionCanReceiveCredit()
    {
        var exception = TestQuestionFactory.Answer(
            "phonetic-exception",
            "かんじ",
            AcceptedAnswerVariantType.PhoneticException);
        var question = TestQuestionFactory.ExactText(
            canonicalText: "漢字",
            allowNonKanji: false,
            additionalAnswers: [exception]);

        var result = DeterministicGrader.Grade(
            question,
            new AnswerObservation("かんじ"));

        Assert.Equal(GradeDisposition.Correct, result.Disposition);
        Assert.Equal(GradingStage.KanjiPolicy, result.Stage);
        Assert.Equal(GradeReason.PhoneticException, result.Reason);
    }

    [Fact]
    public void AllowNonKanjiDoesNotAcceptUnconfiguredKana()
    {
        var result = DeterministicGrader.Grade(
            TestQuestionFactory.ExactText(
                canonicalText: "漢字",
                allowNonKanji: true),
            new AnswerObservation("かんじ"));

        Assert.Equal(GradeDisposition.Incorrect, result.Disposition);
        Assert.Equal(GradeReason.NoAcceptedMatch, result.Reason);
    }

    [Fact]
    public void ScriptUncertaintyRequiresReviewBeforeKanjiRejection()
    {
        var result = DeterministicGrader.Grade(
            TestQuestionFactory.ExactText(canonicalText: "漢字"),
            new AnswerObservation(
                "かんじ",
                scriptObservationUncertain: true));

        Assert.Equal(GradeDisposition.ReviewRequired, result.Disposition);
        Assert.Equal(GradeReason.KanjiObservationUncertain, result.Reason);
    }

    [Fact]
    public void ExplicitVariantUsesSafeWidthNormalization()
    {
        var variant = TestQuestionFactory.Answer(
            "variant",
            "ABC",
            AcceptedAnswerVariantType.Equivalent);
        var question = TestQuestionFactory.ExactText(
            canonicalText: "DEF",
            allowNonKanji: true,
            additionalAnswers: [variant]);

        var result = DeterministicGrader.Grade(
            question,
            new AnswerObservation("ＡＢＣ"));

        Assert.Equal(GradeDisposition.Correct, result.Disposition);
        Assert.Equal(GradingStage.ExplicitVariant, result.Stage);
    }

    [Theory]
    [InlineData("大阪;東京;大阪")]
    [InlineData("大阪／東京／大阪")]
    [InlineData("大阪\n東京\n大阪")]
    public void OrderInsensitiveAnswerMatchesACompleteComponentMultiset(
        string transcription)
    {
        var result = DeterministicGrader.Grade(
            TestQuestionFactory.ExactText(
                canonicalText: "東京、大阪、大阪",
                allowNonKanji: true,
                answerOrderInsensitive: true),
            new AnswerObservation(transcription));

        Assert.Equal(GradeDisposition.Correct, result.Disposition);
        Assert.Equal(GradeReason.OrderInsensitiveMatch, result.Reason);
    }

    [Theory]
    [InlineData("大阪、東京")]
    [InlineData("大阪、東京、京都")]
    [InlineData("大阪、大阪、東京、東京")]
    public void OrderInsensitiveAnswerRejectsMissingExtraOrWrongDuplicateComponents(
        string transcription)
    {
        var result = DeterministicGrader.Grade(
            TestQuestionFactory.ExactText(
                canonicalText: "東京、大阪、大阪",
                allowNonKanji: true,
                answerOrderInsensitive: true),
            new AnswerObservation(transcription));

        Assert.Equal(GradeDisposition.Incorrect, result.Disposition);
        Assert.Equal(MilliPoints.Zero, result.AwardedPoints);
    }

    [Fact]
    public void OrderStillMattersWhenOnlyCompleteAnswerIsEnabled()
    {
        var result = DeterministicGrader.Grade(
            TestQuestionFactory.ExactText(
                canonicalText: "東京、大阪",
                allowNonKanji: true,
                requiresCompleteAnswer: true,
                answerOrderInsensitive: false),
            new AnswerObservation("大阪、東京"));

        Assert.Equal(GradeDisposition.Incorrect, result.Disposition);
    }

    [Fact]
    public void FullWidthNumericAnswerIsParsedLocally()
    {
        var result = DeterministicGrader.Grade(
            TestQuestionFactory.Numeric(12.5m, NumericFormat.FixedPoint),
            new AnswerObservation("１２．５"));

        Assert.Equal(GradeDisposition.Correct, result.Disposition);
        Assert.Equal(GradingStage.Numeric, result.Stage);
        Assert.Equal(GradeReason.NumericMatch, result.Reason);
        Assert.Equal(2000, result.AwardedPoints.Value);
    }

    [Fact]
    public void EquivalentFullWidthFractionIsAccepted()
    {
        var result = DeterministicGrader.Grade(
            TestQuestionFactory.Numeric(0.5m, NumericFormat.Fraction),
            new AnswerObservation("１／２"));

        Assert.Equal(GradeDisposition.Correct, result.Disposition);
    }

    [Fact]
    public void NumericToleranceDoesNotExistUnlessConfigured()
    {
        var exactResult = DeterministicGrader.Grade(
            TestQuestionFactory.Numeric(10m, NumericFormat.FixedPoint),
            new AnswerObservation("10.01"));
        var toleranceResult = DeterministicGrader.Grade(
            TestQuestionFactory.Numeric(
                10m,
                NumericFormat.FixedPoint,
                absoluteTolerance: 0.02m),
            new AnswerObservation("10.01"));

        Assert.Equal(GradeDisposition.Incorrect, exactResult.Disposition);
        Assert.Equal(GradeDisposition.Correct, toleranceResult.Disposition);
    }

    [Fact]
    public void MissingRequiredUnitRequiresReview()
    {
        var result = DeterministicGrader.Grade(
            TestQuestionFactory.Numeric(
                5m,
                NumericFormat.WholeNumber,
                units: ["cm"],
                unitRequired: true),
            new AnswerObservation("5"));

        Assert.Equal(GradeDisposition.ReviewRequired, result.Disposition);
        Assert.Equal(GradeReason.UnitMissingOrInvalid, result.Reason);
    }

    [Fact]
    public void FullWidthChoiceIsNormalizedAndGraded()
    {
        var result = DeterministicGrader.Grade(
            TestQuestionFactory.Choice(),
            new AnswerObservation("Ａ"));

        Assert.Equal(GradeDisposition.Correct, result.Disposition);
        Assert.Equal(GradingStage.Choice, result.Stage);
    }

    [Theory]
    [InlineData("A.")]
    [InlineData("Ａ．")]
    [InlineData("(A)")]
    [InlineData("[A]")]
    [InlineData("Ⓐ")]
    public void UnambiguousDecoratedChoiceIsNormalizedAndGraded(string transcription)
    {
        var result = DeterministicGrader.Grade(
            TestQuestionFactory.Choice(),
            new AnswerObservation(transcription));

        Assert.Equal(GradeDisposition.Correct, result.Disposition);
        Assert.Equal(GradeReason.ChoiceMatch, result.Reason);
        Assert.Equal("A", result.NormalizedTranscription);
    }

    [Theory]
    [InlineData("㋐")]
    [InlineData("（ア）")]
    [InlineData("ア。")]
    [InlineData("「ア」")]
    public void JapaneseChoiceMarkerIsNormalizedAndGraded(string transcription)
    {
        var result = DeterministicGrader.Grade(
            TestQuestionFactory.Choice(
                correctChoice: "ア",
                allowedChoices: ["ア", "イ", "ウ"]),
            new AnswerObservation(transcription));

        Assert.Equal(GradeDisposition.Correct, result.Disposition);
        Assert.Equal(GradeReason.ChoiceMatch, result.Reason);
        Assert.Equal("ア", result.NormalizedTranscription);
    }

    [Fact]
    public void CircledNumberChoiceIsNormalizedAndGraded()
    {
        var result = DeterministicGrader.Grade(
            TestQuestionFactory.Choice(
                correctChoice: "1",
                allowedChoices: ["1", "2", "3"]),
            new AnswerObservation("①"));

        Assert.Equal(GradeDisposition.Correct, result.Disposition);
        Assert.Equal("1", result.NormalizedTranscription);
    }

    [Fact]
    public void DecoratedWrongChoiceIsDeterministicallyIncorrect()
    {
        var result = DeterministicGrader.Grade(
            TestQuestionFactory.Choice(),
            new AnswerObservation("B."));

        Assert.Equal(GradeDisposition.Incorrect, result.Disposition);
        Assert.Equal(GradeReason.ChoiceMismatch, result.Reason);
        Assert.Equal("B", result.NormalizedTranscription);
        Assert.False(result.RequiresReview);
    }

    [Theory]
    [InlineData("DB")]
    [InlineData("A/B")]
    [InlineData("A or B")]
    [InlineData("(AB)")]
    [InlineData("A..")]
    [InlineData("答えA")]
    public void AmbiguousMultiCharacterChoiceRequiresReview(string transcription)
    {
        var result = DeterministicGrader.Grade(
            TestQuestionFactory.Choice(),
            new AnswerObservation(transcription));

        Assert.Equal(GradeDisposition.ReviewRequired, result.Disposition);
        Assert.Equal(GradeReason.ChoiceUnrecognized, result.Reason);
        Assert.True(result.RequiresReview);
    }

    [Fact]
    public void DecoratedChoiceThatMatchesMultipleConfiguredLabelsRequiresReview()
    {
        var result = DeterministicGrader.Grade(
            TestQuestionFactory.Choice(
                correctChoice: "A",
                allowedChoices: ["A", "A.", "B"]),
            new AnswerObservation("(A)"));

        Assert.Equal(GradeDisposition.ReviewRequired, result.Disposition);
        Assert.Equal(GradeReason.ChoiceUnrecognized, result.Reason);
    }

    [Fact]
    public void RecognizedWrongChoiceIsIncorrect()
    {
        var result = DeterministicGrader.Grade(
            TestQuestionFactory.Choice(),
            new AnswerObservation("B"));

        Assert.Equal(GradeDisposition.Incorrect, result.Disposition);
        Assert.False(result.RequiresReview);
    }

    [Fact]
    public void UnrecognizedChoiceRequiresReview()
    {
        var result = DeterministicGrader.Grade(
            TestQuestionFactory.Choice(),
            new AnswerObservation("D"));

        Assert.Equal(GradeDisposition.ReviewRequired, result.Disposition);
        Assert.Equal(GradeReason.ChoiceUnrecognized, result.Reason);
    }

    [Fact]
    public void RubricProposalIsBoundedAndAlwaysReviewed()
    {
        var question = RubricQuestion();
        var result = DeterministicGrader.Grade(
            question,
            new AnswerObservation(
                "説明",
                rubricAssessments:
                [
                    new RubricRuleAssessment("r1", true),
                    new RubricRuleAssessment("r2", false),
                ]));

        Assert.Equal(GradeDisposition.Partial, result.Disposition);
        Assert.Equal(1000, result.AwardedPoints.Value);
        Assert.True(result.RequiresReview);
        Assert.Equal(GradeReason.RubricProposal, result.Reason);
    }

    [Fact]
    public void CompleteAnswerQuestionCoercesPartialRubricAwardToZero()
    {
        var question = RubricQuestion(requiresCompleteAnswer: true);
        var result = DeterministicGrader.Grade(
            question,
            new AnswerObservation(
                "説明",
                rubricAssessments:
                [
                    new RubricRuleAssessment("r1", true),
                    new RubricRuleAssessment("r2", false),
                ]));

        Assert.Equal(GradeDisposition.Incorrect, result.Disposition);
        Assert.Equal(MilliPoints.Zero, result.AwardedPoints);
        Assert.Equal(GradeReason.CompleteAnswerRequired, result.Reason);
        Assert.True(result.RequiresReview);
    }

    [Fact]
    public void MissingRubricAssessmentRequiresReviewWithoutGuessing()
    {
        var result = DeterministicGrader.Grade(
            RubricQuestion(),
            new AnswerObservation(
                "説明",
                rubricAssessments: [new RubricRuleAssessment("r1", true)]));

        Assert.Equal(GradeDisposition.ReviewRequired, result.Disposition);
        Assert.Equal(GradeReason.RubricAssessmentInvalid, result.Reason);
        Assert.Equal(MilliPoints.Zero, result.AwardedPoints);
    }

    private static QuestionDefinition RubricQuestion(
        bool requiresCompleteAnswer = false) =>
        new(
            "q-rubric",
            "logical-q-rubric",
            0,
            "問記",
            "説明しなさい。",
            QuestionType.SemanticShortText,
            GradingMode.AiRubric,
            new MilliPoints(2000),
            new MilliPoints(1000),
            allowNonKanji: true,
            requiresReviewAlways: true,
            teacherVerified: true,
            rubricRules:
            [
                new RubricRule(
                    "r1",
                    0,
                    RubricConditionType.ModelAssessed,
                    "要素1",
                    new MilliPoints(1000),
                    teacherVerified: true),
                new RubricRule(
                    "r2",
                    1,
                    RubricConditionType.ModelAssessed,
                    "要素2",
                    new MilliPoints(1000),
                    teacherVerified: true),
            ],
            requiresCompleteAnswer: requiresCompleteAnswer);
}
