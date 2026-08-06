using System.Text.Json;
using OokiGrader.Application.Grading;
using OokiGrader.Domain.Scoring;
using OokiGrader.Domain.Templates;
using DomainQuestionDefinition = OokiGrader.Domain.Templates.QuestionDefinition;

namespace OokiGrader.Application.Tests;

public sealed class AiGradingResponseValidatorTests
{
    [Fact]
    public void ValidateAcceptsExactResultThatAgreesWithLocalRules()
    {
        var question = ExactQuestion();
        using var response = ParseResponse(
            question.Id,
            transcription: "漢字",
            proposedOutcome: "correct",
            proposedPointsMilli: 1_000,
            reviewRecommended: false);

        var validated = AiGradingResponseValidator.Validate(
            response.RootElement,
            "grade-request-1",
            new Dictionary<string, DomainQuestionDefinition>
            {
                [question.Id] = question,
            });

        var observation = Assert.Single(validated.Observations);
        Assert.Equal("漢字", observation.Observation.Transcription);
        Assert.Equal(1_000, observation.ProposedPointsMilli);
        Assert.False(observation.ProviderReviewRecommended);
    }

    [Fact]
    public void ValidateRecomputesResultThatContradictsDeterministicGrader()
    {
        var question = ExactQuestion();
        using var response = ParseResponse(
            question.Id,
            transcription: "漢字",
            proposedOutcome: "incorrect",
            proposedPointsMilli: 0,
            reviewRecommended: false);

        var validated = AiGradingResponseValidator.Validate(
            response.RootElement,
            "grade-request-1",
            new Dictionary<string, DomainQuestionDefinition>
            {
                [question.Id] = question,
            });

        var observation = Assert.Single(validated.Observations);
        Assert.Equal(1_000, observation.ProposedPointsMilli);
        Assert.Equal("correct", observation.ProposedOutcome);
        Assert.True(observation.ProviderReviewRecommended);
        Assert.Equal("ai_deterministic_recomputed", observation.ProviderReasonCode);
    }

    [Theory]
    [InlineData("A.", "correct", 1_000)]
    [InlineData("B.", "incorrect", 0)]
    [InlineData("Ⓐ", "correct", 1_000)]
    public void ValidateAcceptsUnambiguousDecoratedChoiceResult(
        string transcription,
        string proposedOutcome,
        long proposedPointsMilli)
    {
        var question = ChoiceQuestion();
        using var response = ParseResponse(
            question.Id,
            transcription,
            proposedOutcome,
            proposedPointsMilli,
            reviewRecommended: false);

        var validated = AiGradingResponseValidator.Validate(
            response.RootElement,
            "grade-request-1",
            new Dictionary<string, DomainQuestionDefinition>
            {
                [question.Id] = question,
            });

        var observation = Assert.Single(validated.Observations);
        Assert.Equal(proposedOutcome, observation.ProposedOutcome);
        Assert.Equal(proposedPointsMilli, observation.ProposedPointsMilli);
        Assert.False(observation.ProviderReviewRecommended);
        Assert.Null(observation.ProviderReasonCode);
    }

    [Theory]
    [InlineData("DB")]
    [InlineData("A/B")]
    [InlineData("A or B")]
    public void ValidateRoutesAmbiguousChoiceToReview(string transcription)
    {
        var question = ChoiceQuestion();
        using var response = ParseResponse(
            question.Id,
            transcription,
            proposedOutcome: "incorrect",
            proposedPointsMilli: 0,
            reviewRecommended: false);

        var validated = AiGradingResponseValidator.Validate(
            response.RootElement,
            "grade-request-1",
            new Dictionary<string, DomainQuestionDefinition>
            {
                [question.Id] = question,
            });

        var observation = Assert.Single(validated.Observations);
        Assert.Equal("review", observation.ProposedOutcome);
        Assert.Equal(0, observation.ProposedPointsMilli);
        Assert.True(observation.ProviderReviewRecommended);
        Assert.Equal(
            "ai_deterministic_review_required",
            observation.ProviderReasonCode);
    }

    [Fact]
    public void ValidateQuarantinesAiRubricPointValueOutsideConfiguredIncrement()
    {
        var question = RubricQuestion();
        using var response = ParseResponse(
            question.Id,
            transcription: "説明",
            proposedOutcome: "partial",
            proposedPointsMilli: 250,
            reviewRecommended: true);

        var validated = AiGradingResponseValidator.Validate(
            response.RootElement,
            "grade-request-1",
            new Dictionary<string, DomainQuestionDefinition>
            {
                [question.Id] = question,
            });

        var observation = Assert.Single(validated.Observations);
        Assert.Equal(0, observation.ProposedPointsMilli);
        Assert.Equal("review", observation.ProposedOutcome);
        Assert.True(observation.ProviderReviewRecommended);
        Assert.Equal("ai_invalid_point_award", observation.ProviderReasonCode);
    }

    [Fact]
    public void ValidatePreservesGoodItemWhenAnotherAwardIsQuarantined()
    {
        var exact = ExactQuestion();
        var rubric = RubricQuestion();
        using var response = JsonDocument.Parse(
            JsonSerializer.Serialize(new
            {
                schema_version = "answer_transcribe_grade_v1",
                request_key = "grade-request-1",
                results = new[]
                {
                    new
                    {
                        question_id = exact.Id,
                        transcription = "漢字",
                        legibility = "clear",
                        blank = false,
                        proposed_outcome = "correct",
                        proposed_points_milli = 1_000,
                        confidence = 0.98,
                    },
                    new
                    {
                        question_id = rubric.Id,
                        transcription = "説明",
                        legibility = "clear",
                        blank = false,
                        proposed_outcome = "partial",
                        proposed_points_milli = 250,
                        confidence = 0.75,
                    },
                },
                missing_question_ids = Array.Empty<string>(),
                unexpected_content = false,
            }));

        var validated = AiGradingResponseValidator.Validate(
            response.RootElement,
            "grade-request-1",
            new Dictionary<string, DomainQuestionDefinition>
            {
                [exact.Id] = exact,
                [rubric.Id] = rubric,
            });

        Assert.Collection(
            validated.Observations,
            good =>
            {
                Assert.Equal(exact.Id, good.QuestionId);
                Assert.Equal(1_000, good.ProposedPointsMilli);
                Assert.Equal("correct", good.ProposedOutcome);
                Assert.Null(good.ProviderReasonCode);
            },
            quarantined =>
            {
                Assert.Equal(rubric.Id, quarantined.QuestionId);
                Assert.Equal(0, quarantined.ProposedPointsMilli);
                Assert.Equal("review", quarantined.ProposedOutcome);
                Assert.Equal(
                    "ai_invalid_point_award",
                    quarantined.ProviderReasonCode);
            });
    }

    [Fact]
    public void ValidateAcceptsLocatedBlankAsExplicitResult()
    {
        var question = ExactQuestion();
        using var response = ParseResponse(
            question.Id,
            transcription: string.Empty,
            proposedOutcome: "blank",
            proposedPointsMilli: 0,
            reviewRecommended: false,
            blank: true);

        var validated = AiGradingResponseValidator.Validate(
            response.RootElement,
            "grade-request-1",
            new Dictionary<string, DomainQuestionDefinition>
            {
                [question.Id] = question,
            });

        var observation = Assert.Single(validated.Observations);
        Assert.True(observation.Observation.ExplicitlyBlank);
        Assert.Equal(string.Empty, observation.Observation.Transcription);
        Assert.Equal("blank", observation.ProposedOutcome);
        Assert.Equal(0, observation.ProposedPointsMilli);
        Assert.Null(observation.ProviderReasonCode);
    }

    [Fact]
    public void ValidateAcceptsTheMinimalStructuredResponse()
    {
        var question = ExactQuestion();
        using var response = ParseResponse(
            question.Id,
            "漢字",
            "correct",
            1_000,
            reviewRecommended: false);

        var validated = AiGradingResponseValidator.Validate(
            response.RootElement,
            "grade-request-1",
            new Dictionary<string, DomainQuestionDefinition>
            {
                [question.Id] = question,
            });

        Assert.Single(validated.Observations);
    }

    private static DomainQuestionDefinition ExactQuestion() =>
        new(
            "q-exact",
            "logical-q-exact",
            0,
            "問1",
            "漢字で書きなさい。",
            QuestionType.ExactShortText,
            GradingMode.TranscribeThenRules,
            new MilliPoints(1_000),
            new MilliPoints(500),
            allowNonKanji: false,
            requiresReviewAlways: false,
            teacherVerified: true,
            acceptedAnswers:
            [
                new AcceptedAnswer(
                    "answer-1",
                    "漢字",
                    AcceptedAnswerVariantType.Canonical,
                    AnswerProvenance.TeacherEntered,
                    teacherVerified: true),
            ]);

    private static DomainQuestionDefinition ChoiceQuestion() =>
        new(
            "q-choice",
            "logical-q-choice",
            0,
            "問選",
            "選びなさい。",
            QuestionType.MultipleChoice,
            GradingMode.Deterministic,
            new MilliPoints(1_000),
            new MilliPoints(1_000),
            allowNonKanji: true,
            requiresReviewAlways: false,
            teacherVerified: true,
            acceptedAnswers:
            [
                new AcceptedAnswer(
                    "answer-choice",
                    "A",
                    AcceptedAnswerVariantType.Canonical,
                    AnswerProvenance.TeacherEntered,
                    teacherVerified: true),
            ],
            choicePolicy: new ChoiceAnswerPolicy("A", ["A", "B", "C"]));

    private static DomainQuestionDefinition RubricQuestion() =>
        new(
            "q-rubric",
            "logical-q-rubric",
            1,
            "問2",
            "理由を書きなさい。",
            QuestionType.Subjective,
            GradingMode.AiRubric,
            new MilliPoints(1_000),
            new MilliPoints(500),
            allowNonKanji: true,
            requiresReviewAlways: true,
            teacherVerified: true);

    private static JsonDocument ParseResponse(
        string questionId,
        string transcription,
        string proposedOutcome,
        long proposedPointsMilli,
        bool reviewRecommended,
        bool blank = false) =>
        JsonDocument.Parse(
            $$"""
            {
              "schema_version": "answer_transcribe_grade_v1",
              "request_key": "grade-request-1",
              "results": [
                {
                  "question_id": "{{questionId}}",
                  "transcription": "{{transcription}}",
                  "legibility": "clear",
                  "blank": {{blank.ToString().ToLowerInvariant()}},
                  "proposed_outcome": "{{proposedOutcome}}",
                  "proposed_points_milli": {{proposedPointsMilli}},
                  "confidence": 0.98
                }
              ],
              "missing_question_ids": [],
              "unexpected_content": false
            }
            """);
}
