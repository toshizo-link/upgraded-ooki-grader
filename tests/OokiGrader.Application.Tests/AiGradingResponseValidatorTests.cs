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

    [Theory]
    [InlineData("correct", 1_000)]
    [InlineData("incorrect", 0)]
    public void ValidateAllowsClearHighConfidenceAiRubricWithoutMandatoryReview(
        string outcome,
        long points)
    {
        var question = RubricQuestion(requiresReviewAlways: false);
        using var response = ParseResponse(
            question.Id,
            transcription: "説明",
            proposedOutcome: outcome,
            proposedPointsMilli: points,
            reviewRecommended: false);

        var validated = AiGradingResponseValidator.Validate(
            response.RootElement,
            "grade-request-1",
            new Dictionary<string, DomainQuestionDefinition>
            {
                [question.Id] = question,
            });

        var observation = Assert.Single(validated.Observations);
        Assert.Equal(outcome, observation.ProposedOutcome);
        Assert.Equal(points, observation.ProposedPointsMilli);
        Assert.False(observation.ProviderReviewRecommended);
    }

    [Fact]
    public void ValidateRepairsAiRubricFalseNegativeCausedOnlyByVisibleLineWrapping()
    {
        var question = AiRubricQuestionWithAnswer("おしべの先\n(やく)");
        using var response = ParseSubmissionAnalysisResponse(
            question.Id,
            transcription: "おしべの先(やく)",
            proposedOutcome: "incorrect",
            proposedPointsMilli: 0);

        var validated = AiGradingResponseValidator.Validate(
            response.RootElement,
            "grade-request-1",
            new Dictionary<string, DomainQuestionDefinition>
            {
                [question.Id] = question,
            },
            mediaPartCount: 1);

        var observation = Assert.Single(validated.Observations);
        Assert.Equal(1_000, observation.ProposedPointsMilli);
        Assert.Equal("correct", observation.ProposedOutcome);
        Assert.False(observation.ProviderReviewRecommended);
        Assert.Equal(
            "ai_layout_line_wrap_reconciled",
            observation.ProviderReasonCode);
    }

    [Theory]
    [InlineData("New York", "NewYork", false)]
    [InlineData("赤\n青", "赤青", true)]
    public void ValidateDoesNotBroadenLineWrapRepairBeyondSafeAiRubricCase(
        string acceptedAnswer,
        string transcription,
        bool answerOrderInsensitive)
    {
        var question = AiRubricQuestionWithAnswer(
            acceptedAnswer,
            answerOrderInsensitive);
        using var response = ParseSubmissionAnalysisResponse(
            question.Id,
            transcription,
            proposedOutcome: "incorrect",
            proposedPointsMilli: 0);

        var validated = AiGradingResponseValidator.Validate(
            response.RootElement,
            "grade-request-1",
            new Dictionary<string, DomainQuestionDefinition>
            {
                [question.Id] = question,
            },
            mediaPartCount: 1);

        var observation = Assert.Single(validated.Observations);
        Assert.Equal(0, observation.ProposedPointsMilli);
        Assert.Equal("incorrect", observation.ProposedOutcome);
        Assert.Null(observation.ProviderReasonCode);
    }

    [Fact]
    public void ValidateHonorsPermanentReviewForAiRubric()
    {
        var question = RubricQuestion(requiresReviewAlways: true);
        using var response = ParseResponse(
            question.Id,
            transcription: "説明",
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

        Assert.True(Assert.Single(validated.Observations)
            .ProviderReviewRecommended);
    }

    [Fact]
    public void ValidateRoutesLowConfidenceAiRubricToReview()
    {
        var question = RubricQuestion(requiresReviewAlways: false);
        using var response = ParseResponse(
            question.Id,
            transcription: "説明",
            proposedOutcome: "correct",
            proposedPointsMilli: 1_000,
            reviewRecommended: false,
            confidence: 0.79);

        var validated = AiGradingResponseValidator.Validate(
            response.RootElement,
            "grade-request-1",
            new Dictionary<string, DomainQuestionDefinition>
            {
                [question.Id] = question,
            });

        Assert.True(Assert.Single(validated.Observations)
            .ProviderReviewRecommended);
    }

    [Fact]
    public void ValidateCoercesCompleteAnswerPartialAwardToZero()
    {
        var question = RubricQuestion(requiresCompleteAnswer: true);
        using var response = ParseResponse(
            question.Id,
            transcription: "要素Aのみ",
            proposedOutcome: "partial",
            proposedPointsMilli: 500,
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
        Assert.Equal("incorrect", observation.ProposedOutcome);
        Assert.True(observation.ProviderReviewRecommended);
        Assert.Equal(
            "ai_complete_answer_required",
            observation.ProviderReasonCode);
    }

    [Fact]
    public void ValidateLetsLocalCorrectAnswerOverrideCompleteAnswerPartialProposal()
    {
        var question = ExactQuestion(requiresCompleteAnswer: true);
        using var response = ParseResponse(
            question.Id,
            transcription: "漢字",
            proposedOutcome: "partial",
            proposedPointsMilli: 500,
            reviewRecommended: true);

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
        Assert.Equal(
            "ai_deterministic_recomputed",
            observation.ProviderReasonCode);
    }

    [Fact]
    public void ValidatePreservesUnreadableReviewForCompleteAnswerQuestion()
    {
        var question = RubricQuestion(requiresCompleteAnswer: true);
        using var response = ParseResponse(
            question.Id,
            transcription: "判読不能",
            proposedOutcome: "unreadable",
            proposedPointsMilli: 0,
            reviewRecommended: true,
            legibility: "unreadable");

        var validated = AiGradingResponseValidator.Validate(
            response.RootElement,
            "grade-request-1",
            new Dictionary<string, DomainQuestionDefinition>
            {
                [question.Id] = question,
            });

        var observation = Assert.Single(validated.Observations);
        Assert.Equal(0, observation.ProposedPointsMilli);
        Assert.Equal("unreadable", observation.ProposedOutcome);
        Assert.True(observation.ProviderReviewRecommended);
        Assert.Equal(
            "ai_deterministic_review_required",
            observation.ProviderReasonCode);
    }

    [Fact]
    public void ValidatePreservesAmbiguousReviewDespiteCompleteAnswerPartialProposal()
    {
        var question = RubricQuestion(requiresCompleteAnswer: true);
        using var response = ParseResponse(
            question.Id,
            transcription: "要素Aかもしれない",
            proposedOutcome: "partial",
            proposedPointsMilli: 500,
            reviewRecommended: true,
            legibility: "ambiguous");

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
        Assert.Equal(
            "ai_deterministic_review_required",
            observation.ProviderReasonCode);
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

    private static DomainQuestionDefinition ExactQuestion(
        bool requiresCompleteAnswer = false) =>
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
            ],
            requiresCompleteAnswer: requiresCompleteAnswer);

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

    private static DomainQuestionDefinition RubricQuestion(
        bool requiresCompleteAnswer = false,
        bool requiresReviewAlways = true) =>
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
            requiresReviewAlways,
            teacherVerified: true,
            requiresCompleteAnswer: requiresCompleteAnswer);

    private static DomainQuestionDefinition AiRubricQuestionWithAnswer(
        string answerText,
        bool answerOrderInsensitive = false) =>
        new(
            "q-ai-layout",
            "logical-q-ai-layout",
            2,
            "問3",
            "答えを書きなさい。",
            QuestionType.ExactShortText,
            GradingMode.AiRubric,
            new MilliPoints(1_000),
            new MilliPoints(1_000),
            allowNonKanji: true,
            requiresReviewAlways: false,
            teacherVerified: true,
            acceptedAnswers:
            [
                new AcceptedAnswer(
                    "answer-ai-layout",
                    answerText,
                    AcceptedAnswerVariantType.Canonical,
                    AnswerProvenance.TeacherEntered,
                    teacherVerified: true),
            ],
            answerOrderInsensitive: answerOrderInsensitive);

    private static JsonDocument ParseSubmissionAnalysisResponse(
        string questionId,
        string transcription,
        string proposedOutcome,
        long proposedPointsMilli) =>
        JsonDocument.Parse(
            JsonSerializer.Serialize(new
            {
                schema_version = "submission_analysis_v2",
                request_key = "grade-request-1",
                identity = (object?)null,
                results = new[]
                {
                    new
                    {
                        question_id = questionId,
                        evidence_media_index = 0,
                        transcription,
                        legibility = "clear",
                        blank = false,
                        proposed_outcome = proposedOutcome,
                        proposed_points_milli = proposedPointsMilli,
                        confidence = 0.98,
                    },
                },
                missing_question_ids = Array.Empty<string>(),
                unexpected_content = false,
            }));

    private static JsonDocument ParseResponse(
        string questionId,
        string transcription,
        string proposedOutcome,
        long proposedPointsMilli,
        bool reviewRecommended,
        bool blank = false,
        string legibility = "clear",
        double confidence = 0.98) =>
        JsonDocument.Parse(
            $$"""
            {
              "schema_version": "answer_transcribe_grade_v1",
              "request_key": "grade-request-1",
              "results": [
                {
                  "question_id": "{{questionId}}",
                  "transcription": "{{transcription}}",
                  "legibility": "{{legibility}}",
                  "blank": {{blank.ToString().ToLowerInvariant()}},
                  "proposed_outcome": "{{proposedOutcome}}",
                  "proposed_points_milli": {{proposedPointsMilli}},
                  "confidence": {{confidence.ToString(System.Globalization.CultureInfo.InvariantCulture)}}
                }
              ],
              "missing_question_ids": [],
              "unexpected_content": false
            }
            """);
}
