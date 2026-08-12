using OokiGrader.Domain.Common;
using OokiGrader.Domain.Scoring;
using OokiGrader.Domain.Templates;

namespace OokiGrader.Domain.Tests;

public sealed class TemplateVersionTests
{
    private static readonly DateTimeOffset PublishTime =
        new(2026, 7, 27, 9, 0, 0, TimeSpan.FromHours(9));

    [Fact]
    public void PublishCreatesImmutablePublishedSnapshotAndContentHash()
    {
        var draft = ValidDraft();

        var published = draft.Publish("teacher-1", PublishTime);

        Assert.Equal(TemplateVersionState.Draft, draft.State);
        Assert.Null(draft.ContentHash);
        Assert.Equal(TemplateVersionState.Published, published.State);
        Assert.Equal("teacher-1", published.PublishedBy);
        Assert.Equal(PublishTime, published.PublishedAt);
        Assert.Matches("^[0-9a-f]{64}$", published.ContentHash);
        Assert.Equal(draft.Questions, published.Questions);
    }

    [Fact]
    public void PublishedVersionCannotBeEdited()
    {
        var published = ValidDraft().Publish("teacher-1", PublishTime);

        Assert.Throws<InvalidDomainStateException>(
            () => published.WithQuestion(
                TestQuestionFactory.ExactText(
                    id: "q-2",
                    orderIndex: 1,
                    displayLabel: "問2")));
        Assert.Single(published.Questions);
    }

    [Fact]
    public void DraftEditReturnsNewRevisionWithoutMutatingOriginal()
    {
        var draft = ValidDraft();
        var edited = draft.WithQuestion(
            TestQuestionFactory.ExactText(
                id: "q-2",
                orderIndex: 1,
                displayLabel: "問2"));

        Assert.Single(draft.Questions);
        Assert.Equal(2, edited.Questions.Count);
        Assert.Equal(0, draft.Revision);
        Assert.Equal(1, edited.Revision);
    }

    [Fact]
    public void UnverifiedQuestionBlocksPublish()
    {
        var draft = TemplateVersion.CreateDraft(
            "version-1",
            "template-1",
            1,
            "pipeline-v1",
            [TestQuestionFactory.ExactText(teacherVerified: false)]);

        var result = draft.TryPublish("teacher-1", PublishTime);

        Assert.True(result.IsFailure);
        Assert.Contains(
            result.Errors,
            error => error.Code == "question.not_teacher_verified");
    }

    [Fact]
    public void UnverifiedAcceptedAnswerBlocksPublish()
    {
        var unverified = TestQuestionFactory.Answer(
            "q-1-canonical",
            "漢字",
            AcceptedAnswerVariantType.Canonical,
            teacherVerified: false);
        var question = new QuestionDefinition(
            "q-1",
            "logical-q-1",
            0,
            "問1",
            "書きなさい。",
            QuestionType.ExactShortText,
            GradingMode.TranscribeThenRules,
            new MilliPoints(1000),
            new MilliPoints(1000),
            allowNonKanji: false,
            requiresReviewAlways: false,
            teacherVerified: true,
            acceptedAnswers: [unverified]);
        var draft = TemplateVersion.CreateDraft(
            "version-1",
            "template-1",
            1,
            "pipeline-v1",
            [question]);

        var validation = draft.ValidateForPublish();

        Assert.Contains(
            validation.Errors,
            error => error.Code == "answer.not_teacher_verified");
    }

    [Theory]
    [InlineData(TemplateSourceRole.BlankTest)]
    [InlineData(TemplateSourceRole.ContainsNonModelAnswers)]
    public void ProvidedModelAnswerRequiresExplicitAuthoritativeSourceRole(
        TemplateSourceRole sourceRole)
    {
        var answer = TestQuestionFactory.Answer(
            "q-1-canonical",
            "漢字",
            AcceptedAnswerVariantType.Canonical,
            provenance: AnswerProvenance.ProvidedModelAnswer,
            source: new AnswerSourceReference(
                "non-authoritative-source",
                sourceRole,
                1));
        var draft = DraftWithAnswer(answer);

        var validation = draft.ValidateForPublish();

        Assert.Contains(
            validation.Errors,
            error => error.Code == "answer.invalid_provided_source");
    }

    [Theory]
    [InlineData(TemplateSourceRole.ContainsModelAnswers)]
    [InlineData(TemplateSourceRole.SeparateAnswerKey)]
    public void ProvidedModelAnswerAcceptsAuthoritativeSourceRoles(
        TemplateSourceRole sourceRole)
    {
        var answer = TestQuestionFactory.Answer(
            "q-1-canonical",
            "漢字",
            AcceptedAnswerVariantType.Canonical,
            provenance: AnswerProvenance.ProvidedModelAnswer,
            source: new AnswerSourceReference("answer-source", sourceRole, 1));

        var validation = DraftWithAnswer(answer).ValidateForPublish();

        Assert.True(validation.IsValid);
    }

    [Fact]
    public void TargetTotalMismatchBlocksPublish()
    {
        var draft = TemplateVersion.CreateDraft(
            "version-1",
            "template-1",
            1,
            "pipeline-v1",
            [TestQuestionFactory.ExactText()],
            targetTotalPoints: new MilliPoints(2000));

        var validation = draft.ValidateForPublish();

        Assert.Contains(
            validation.Errors,
            error => error.Code == "template.target_total_mismatch");
    }

    [Fact]
    public void DuplicateQuestionLabelsBlockPublish()
    {
        var draft = TemplateVersion.CreateDraft(
            "version-1",
            "template-1",
            1,
            "pipeline-v1",
            [
                TestQuestionFactory.ExactText(),
                TestQuestionFactory.ExactText(
                    id: "q-2",
                    orderIndex: 1,
                    displayLabel: "問1"),
            ]);

        var validation = draft.ValidateForPublish();

        Assert.Contains(
            validation.Errors,
            error => error.Code == "template.duplicate_display_label");
    }

    [Fact]
    public void GenerationLifecycleIsExplicit()
    {
        var draft = ValidDraft();

        var generating = draft.BeginGeneration();
        var completed = generating.CompleteGeneration(
            [
                TestQuestionFactory.ExactText(
                    id: "q-generated",
                    displayLabel: "問自動"),
            ],
            "proposal-1");

        Assert.Equal(TemplateVersionState.Draft, draft.State);
        Assert.Equal(TemplateVersionState.Generating, generating.State);
        Assert.Equal(TemplateVersionState.Draft, completed.State);
        Assert.Equal("proposal-1", completed.AiGenerationProvenanceId);
        Assert.Equal("q-generated", completed.Questions.Single().Id);
    }

    [Fact]
    public void ConfirmQuestionProposalsVerifiesOnlySelectedGeneratedContent()
    {
        var unverified = new QuestionDefinition(
            "q-generated",
            "logical-generated",
            0,
            "問自動",
            "理由を説明しなさい。",
            QuestionType.SemanticShortText,
            GradingMode.AiRubric,
            new MilliPoints(1000),
            new MilliPoints(1000),
            allowNonKanji: true,
            requiresReviewAlways: true,
            teacherVerified: false,
            acceptedAnswers:
            [
                TestQuestionFactory.Answer(
                    "answer-generated",
                    "提案",
                    AcceptedAnswerVariantType.Canonical,
                    provenance: AnswerProvenance.AiProposed,
                    teacherVerified: false),
            ],
            rubricRules:
            [
                new RubricRule(
                    "rubric-generated",
                    0,
                    RubricConditionType.ModelAssessed,
                    "根拠を一つ含む。",
                    new MilliPoints(1000),
                    teacherVerified: false),
            ]);
        var untouched = TestQuestionFactory.ExactText(
            id: "q-untouched",
            orderIndex: 1,
            displayLabel: "問2",
            teacherVerified: false,
            additionalAnswers:
            [
                TestQuestionFactory.Answer(
                    "answer-untouched",
                    "別案",
                    AcceptedAnswerVariantType.Equivalent,
                    teacherVerified: false),
            ]);
        var draft = TemplateVersion.CreateDraft(
            "version-1",
            "template-1",
            1,
            "pipeline-v1",
            [unverified, untouched],
            aiGenerationProvenanceId: "ai-request-1");

        var confirmed = draft.ConfirmQuestionProposals(["q-generated"]);

        Assert.False(draft.Questions[0].TeacherVerified);
        Assert.Equal(0, draft.Revision);
        var confirmedQuestion = confirmed.Questions.Single(
            question => question.Id == "q-generated");
        Assert.True(confirmedQuestion.TeacherVerified);
        Assert.True(confirmedQuestion.RequiresReviewAlways);
        Assert.All(
            confirmedQuestion.AcceptedAnswers,
            answer => Assert.True(answer.TeacherVerified));
        Assert.All(
            confirmedQuestion.RubricRules,
            rule => Assert.True(rule.TeacherVerified));
        Assert.False(
            confirmed.Questions.Single(question => question.Id == "q-untouched")
                .TeacherVerified);
        Assert.Equal(1, confirmed.Revision);
    }

    [Fact]
    public void ConfirmQuestionProposalsRejectsQuestionsOutsideDraft()
    {
        var draft = ValidDraft();

        var exception = Assert.Throws<DomainValidationException>(
            () => draft.ConfirmQuestionProposals(["missing"]));

        Assert.Contains(
            exception.Errors,
            error => error.Code == "template.question_not_found");
        var published = draft.Publish("teacher-1", PublishTime);
        Assert.Throws<InvalidDomainStateException>(
            () => published.ConfirmQuestionProposals(["q-1"]));
    }

    [Fact]
    public void PublishedVersionCanOnlyChangeLifecycleOrClone()
    {
        var published = ValidDraft().Publish("teacher-1", PublishTime);
        var superseded = published.MarkSuperseded();
        var retired = superseded.Retire();
        var clone = retired.CloneAsDraft("version-2", 2, "pipeline-v2");

        Assert.Equal(TemplateVersionState.Published, published.State);
        Assert.Equal(TemplateVersionState.Superseded, superseded.State);
        Assert.Equal(TemplateVersionState.Retired, retired.State);
        Assert.Equal(TemplateVersionState.Draft, clone.State);
        Assert.Equal(published.Id, clone.BasedOnVersionId);
        Assert.Equal(2, clone.VersionNumber);
        Assert.Null(clone.ContentHash);
    }

    [Fact]
    public void IdenticalContentProducesStableHash()
    {
        var first = ValidDraft().Publish("teacher-1", PublishTime);
        var second = ValidDraft().Publish("teacher-2", PublishTime.AddHours(1));

        Assert.Equal(first.ContentHash, second.ContentHash);
    }

    [Fact]
    public void GradingRuleFlagsParticipateInPublishedContentHash()
    {
        var baseline = ValidDraft().Publish("teacher-1", PublishTime);
        var complete = DraftWithRuleFlags(
            requiresCompleteAnswer: true,
            answerOrderInsensitive: false).Publish("teacher-1", PublishTime);
        var unordered = DraftWithRuleFlags(
            requiresCompleteAnswer: false,
            answerOrderInsensitive: true).Publish("teacher-1", PublishTime);

        Assert.NotEqual(baseline.ContentHash, complete.ContentHash);
        Assert.NotEqual(baseline.ContentHash, unordered.ContentHash);
        Assert.NotEqual(complete.ContentHash, unordered.ContentHash);
    }

    [Fact]
    public void RubricTotalCannotExceedQuestionMaximum()
    {
        var question = new QuestionDefinition(
            "q-rubric",
            "logical-q-rubric",
            0,
            "問記",
            "説明しなさい。",
            QuestionType.SemanticShortText,
            GradingMode.AiRubric,
            new MilliPoints(1000),
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
                    "要素",
                    new MilliPoints(2000),
                    teacherVerified: true),
            ]);
        var draft = TemplateVersion.CreateDraft(
            "version-1",
            "template-1",
            1,
            "pipeline-v1",
            [question]);

        Assert.Contains(
            draft.ValidateForPublish().Errors,
            error => error.Code == "rubric.above_maximum");
    }

    [Fact]
    public void NumericQuestionRequiresExplicitNumericPolicy()
    {
        var question = new QuestionDefinition(
            "q-numeric",
            "logical-q-numeric",
            0,
            "問数",
            "数を書きなさい。",
            QuestionType.Numeric,
            GradingMode.TranscribeThenRules,
            new MilliPoints(1000),
            new MilliPoints(1000),
            allowNonKanji: true,
            requiresReviewAlways: false,
            teacherVerified: true,
            acceptedAnswers:
            [
                TestQuestionFactory.Answer(
                    "canonical",
                    "1",
                    AcceptedAnswerVariantType.Canonical),
            ]);
        var draft = TemplateVersion.CreateDraft(
            "version-1",
            "template-1",
            1,
            "pipeline-v1",
            [question]);

        Assert.Contains(
            draft.ValidateForPublish().Errors,
            error => error.Code == "numeric.policy_required");
    }

    [Fact]
    public void RequiredNumericUnitMustBePresentInCanonicalAnswer()
    {
        var question = new QuestionDefinition(
            "q-numeric",
            "logical-q-numeric",
            0,
            "問数",
            "長さを書きなさい。",
            QuestionType.Numeric,
            GradingMode.TranscribeThenRules,
            new MilliPoints(1000),
            new MilliPoints(1000),
            allowNonKanji: true,
            requiresReviewAlways: false,
            teacherVerified: true,
            acceptedAnswers:
            [
                TestQuestionFactory.Answer(
                    "canonical",
                    "5",
                    AcceptedAnswerVariantType.Canonical),
            ],
            numericPolicy: new NumericAnswerPolicy(
                5,
                NumericFormat.WholeNumber,
                acceptedUnits: ["cm"],
                unitRequired: true));
        var draft = TemplateVersion.CreateDraft(
            "version-1",
            "template-1",
            1,
            "pipeline-v1",
            [question]);

        Assert.Contains(
            draft.ValidateForPublish().Errors,
            error => error.Code == "numeric.canonical_policy_mismatch");
    }

    [Fact]
    public void CanonicalChoiceMustMatchChoicePolicy()
    {
        var question = new QuestionDefinition(
            "q-choice",
            "logical-q-choice",
            0,
            "問選",
            "選びなさい。",
            QuestionType.MultipleChoice,
            GradingMode.Deterministic,
            new MilliPoints(1000),
            new MilliPoints(1000),
            allowNonKanji: true,
            requiresReviewAlways: false,
            teacherVerified: true,
            acceptedAnswers:
            [
                TestQuestionFactory.Answer(
                    "canonical",
                    "B",
                    AcceptedAnswerVariantType.Canonical),
            ],
            choicePolicy: new ChoiceAnswerPolicy("A", ["A", "B"]));
        var draft = TemplateVersion.CreateDraft(
            "version-1",
            "template-1",
            1,
            "pipeline-v1",
            [question]);

        Assert.Contains(
            draft.ValidateForPublish().Errors,
            error => error.Code == "choice.canonical_policy_mismatch");
    }

    [Fact]
    public void PointTotalOverflowIsReportedAsPublishValidationError()
    {
        var draft = TemplateVersion.CreateDraft(
            "version-1",
            "template-1",
            1,
            "pipeline-v1",
            [
                TestQuestionFactory.ExactText(
                    id: "q-1",
                    orderIndex: 0,
                    displayLabel: "問1",
                    maximum: new MilliPoints(long.MaxValue),
                    increment: new MilliPoints(1)),
                TestQuestionFactory.ExactText(
                    id: "q-2",
                    orderIndex: 1,
                    displayLabel: "問2",
                    maximum: new MilliPoints(1),
                    increment: new MilliPoints(1)),
            ]);

        Assert.Contains(
            draft.ValidateForPublish().Errors,
            error => error.Code == "template.total_overflow");
    }

    [Theory]
    [InlineData(QuestionType.MultipleChoice, GradingMode.AiRubric)]
    [InlineData(QuestionType.Boolean, GradingMode.AiRubric)]
    [InlineData(QuestionType.Numeric, GradingMode.AiRubric)]
    [InlineData(QuestionType.ExactShortText, GradingMode.AiRubric)]
    [InlineData(QuestionType.SemanticShortText, GradingMode.AiRubric)]
    [InlineData(QuestionType.Subjective, GradingMode.AiRubric)]
    [InlineData(QuestionType.MultiPart, GradingMode.AiRubric)]
    [InlineData(QuestionType.Unsupported, GradingMode.Manual)]
    public void QuestionTypesHaveSafeDefaultGradingModes(
        QuestionType questionType,
        GradingMode expected)
    {
        Assert.Equal(expected, QuestionGradingDefaults.For(questionType));
    }

    [Fact]
    public void SubjectiveQuestionCanUseAiRubricWithoutPermanentReview()
    {
        var valid = SubjectiveQuestion(
            GradingMode.AiRubric,
            requiresReviewAlways: true);
        var automatic = SubjectiveQuestion(
            GradingMode.AiRubric,
            requiresReviewAlways: false);

        Assert.True(valid.ValidateForPublish("questions[0]").IsValid);
        Assert.True(automatic.ValidateForPublish("questions[0]").IsValid);
    }

    [Fact]
    public void UnsupportedQuestionRemainsManualAndAlwaysReviewed()
    {
        var question = new QuestionDefinition(
            "q-unsupported",
            "logical-q-unsupported",
            0,
            "問未",
            "未対応形式",
            QuestionType.Unsupported,
            GradingMode.AiRubric,
            new MilliPoints(1000),
            new MilliPoints(1000),
            allowNonKanji: true,
            requiresReviewAlways: true,
            teacherVerified: true,
            rubricRules:
            [
                new RubricRule(
                    "rubric-unsupported",
                    0,
                    RubricConditionType.ModelAssessed,
                    "確認する。",
                    new MilliPoints(1000),
                    teacherVerified: true),
            ]);

        Assert.Contains(
            question.ValidateForPublish("questions[0]").Errors,
            error => error.Code == "question.manual_review_required");
    }

    private static TemplateVersion ValidDraft() =>
        TemplateVersion.CreateDraft(
            "version-1",
            "template-1",
            1,
            "pipeline-v1",
            [TestQuestionFactory.ExactText()],
            targetTotalPoints: new MilliPoints(1000));

    private static QuestionDefinition SubjectiveQuestion(
        GradingMode gradingMode,
        bool requiresReviewAlways) =>
        new(
            "q-subjective",
            "logical-q-subjective",
            0,
            "問記",
            "理由を説明しなさい。",
            QuestionType.Subjective,
            gradingMode,
            new MilliPoints(1000),
            new MilliPoints(1000),
            allowNonKanji: true,
            requiresReviewAlways,
            teacherVerified: true,
            rubricRules:
            [
                new RubricRule(
                    "rubric-subjective",
                    0,
                    RubricConditionType.ModelAssessed,
                    "模範解答の要点を満たす。",
                    new MilliPoints(1000),
                    teacherVerified: true),
            ]);

    private static TemplateVersion DraftWithRuleFlags(
        bool requiresCompleteAnswer,
        bool answerOrderInsensitive) =>
        TemplateVersion.CreateDraft(
            "version-1",
            "template-1",
            1,
            "pipeline-v1",
            [
                TestQuestionFactory.ExactText(
                    requiresCompleteAnswer: requiresCompleteAnswer,
                    answerOrderInsensitive: answerOrderInsensitive),
            ],
            targetTotalPoints: new MilliPoints(1000));

    private static TemplateVersion DraftWithAnswer(AcceptedAnswer answer)
    {
        var question = new QuestionDefinition(
            "q-1",
            "logical-q-1",
            0,
            "問1",
            "書きなさい。",
            QuestionType.ExactShortText,
            GradingMode.TranscribeThenRules,
            new MilliPoints(1000),
            new MilliPoints(1000),
            allowNonKanji: false,
            requiresReviewAlways: false,
            teacherVerified: true,
            acceptedAnswers: [answer]);
        return TemplateVersion.CreateDraft(
            "version-1",
            "template-1",
            1,
            "pipeline-v1",
            [question]);
    }
}
