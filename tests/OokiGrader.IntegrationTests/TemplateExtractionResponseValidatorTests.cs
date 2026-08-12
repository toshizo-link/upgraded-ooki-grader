using System.Text.Json;
using OokiGrader.Host.Jobs;

namespace OokiGrader.IntegrationTests;

public sealed class TemplateExtractionResponseValidatorTests
{
    private const string RequestKey = "template_validator_fixture";
    private const string SourceId = "source-1";
    private const string MultiBlankText =
        "鏡にあたる前の光を入射光線、［　］したあとの光を［　］光線という。";

    [Fact]
    public void IdenticalAdjacentQuestionsMapPlaceholderBySlotOrdinal()
    {
        var validated = Validate(
            detectedAnswerSlotCount: 2,
            CreateQuestion(
                sourceKey: "page-1-slot-1",
                label: "⑧",
                ordinal: 1,
                questionText: MultiBlankText,
                answer: "反射"),
            CreateQuestion(
                sourceKey: "page-1-slot-2",
                label: "⑧",
                ordinal: 2,
                questionText: MultiBlankText,
                answer: "反射"));

        var questions = Assert.Single(validated.Pages).Questions;
        Assert.Collection(
            questions,
            first =>
            {
                Assert.Equal(
                    "鏡にあたる前の光を入射光線、［　］したあとの光を"
                    + "（別の空欄は省略）光線という。",
                    first.QuestionText);
                Assert.Equal("反射", first.ExpectedAnswer);
                Assert.DoesNotContain(
                    first.ReviewIssues,
                    IsMultiPlaceholderBlocker);
            },
            second =>
            {
                Assert.Equal(
                    "鏡にあたる前の光を入射光線、（別の空欄は省略）"
                    + "したあとの光を［　］光線という。",
                    second.QuestionText);
                Assert.Equal("反射", second.ExpectedAnswer);
                Assert.DoesNotContain(
                    second.ReviewIssues,
                    IsMultiPlaceholderBlocker);
            });
        Assert.False(
            TemplateExtractionResponseValidator
                .HasRepairableSlotStructureIssue(validated));
    }

    [Fact]
    public void ExcessIdenticalCandidatesRemainSafelyBlocked()
    {
        var validated = Validate(
            detectedAnswerSlotCount: 3,
            CreateQuestion("slot-1", "A", 1, MultiBlankText, "反射"),
            CreateQuestion("slot-2", "B", 2, MultiBlankText, "反射"),
            CreateQuestion("slot-3", "C", 3, MultiBlankText, "光"));

        var questions = Assert.Single(validated.Pages).Questions;
        Assert.All(
            questions,
            question =>
            {
                Assert.Equal(1, CountPlaceholders(question.QuestionText));
                Assert.Contains(
                    question.ReviewIssues,
                    issue => issue.Code
                        == "question.additional_placeholders_redacted"
                        && issue.Blocking);
                Assert.Contains(
                    question.ReviewIssues,
                    issue => issue.Code
                        == "question.fill_blank_placeholder_invalid"
                        && issue.Blocking);
            });
        Assert.True(
            TemplateExtractionResponseValidator
                .HasRepairableSlotStructureIssue(validated));
    }

    [Fact]
    public void NonAdjacentIdenticalCandidatesRemainSafelyBlocked()
    {
        var validated = Validate(
            detectedAnswerSlotCount: 3,
            CreateQuestion("slot-1", "A", 1, MultiBlankText, "反射"),
            CreateQuestion(
                "slot-2",
                "B",
                2,
                "光の進み方を［　］という。",
                "直進"),
            CreateQuestion("slot-3", "C", 3, MultiBlankText, "反射"));

        var questions = Assert.Single(validated.Pages).Questions;
        Assert.Contains(
            questions[0].ReviewIssues,
            issue => issue.Code
                == "question.additional_placeholders_redacted"
                && issue.Blocking);
        Assert.DoesNotContain(
            questions[1].ReviewIssues,
            IsMultiPlaceholderBlocker);
        Assert.Contains(
            questions[2].ReviewIssues,
            issue => issue.Code
                == "question.additional_placeholders_redacted"
                && issue.Blocking);
    }

    [Fact]
    public void CarriesIndependentPrintedGradingRuleSuggestions()
    {
        var validated = Validate(
            detectedAnswerSlotCount: 1,
            CreateQuestion(
                "slot-1",
                "A",
                1,
                "完答・順不同で答えなさい。",
                "東京、大阪",
                requiresCompleteAnswer: true,
                answerOrderInsensitive: true));

        var question = Assert.Single(Assert.Single(validated.Pages).Questions);
        Assert.True(question.RequiresCompleteAnswerSuggestion);
        Assert.True(question.AnswerOrderInsensitiveSuggestion);
    }

    [Fact]
    public void LegacyV4QuestionWithoutNewFlagsDefaultsBothToFalse()
    {
        var validated = Validate(
            detectedAnswerSlotCount: 1,
            CreateLegacyV4Question());

        var question = Assert.Single(Assert.Single(validated.Pages).Questions);
        Assert.False(question.RequiresCompleteAnswerSuggestion);
        Assert.False(question.AnswerOrderInsensitiveSuggestion);
    }

    private static ValidatedTemplateExtraction Validate(
        int detectedAnswerSlotCount,
        params object[] questions)
    {
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(new
            {
                schema_version = "template_extract_v4",
                request_key = RequestKey,
                metadata = new
                {
                    title = "理科",
                    subject = "理科",
                    category = "光",
                    grade_label = "中学1年",
                    course = "理科",
                    confidence = 0.99,
                    warnings = Array.Empty<string>(),
                },
                pages = new[]
                {
                    new
                    {
                        source_id = SourceId,
                        page_number = 1,
                        detected_answer_slot_count = detectedAnswerSlotCount,
                        questions,
                    },
                },
                global_warnings = Array.Empty<string>(),
            }));
        return TemplateExtractionResponseValidator.Validate(
            document.RootElement,
            RequestKey,
            new Dictionary<string, TemplateExtractionSourceEvidence>(
                StringComparer.Ordinal)
            {
                [SourceId] = new(
                    SourceId,
                    "contains_non_model_answers",
                    PageCount: 1),
            },
            defaultPointsMilli: 1_000,
            targetTotalPointsMilli: null);
    }

    private static object CreateQuestion(
        string sourceKey,
        string label,
        int ordinal,
        string questionText,
        string answer,
        bool requiresCompleteAnswer = false,
        bool answerOrderInsensitive = false) =>
        new
        {
            source_key = sourceKey,
            display_label = label,
            question_text = questionText,
            answer_slot_ordinal = ordinal,
            answer_slot_count = 1,
            filled_answer_removed = true,
            is_embedded_fill_blank = true,
            question_type = "exact_short_text",
            expected_answer = answer,
            answer_provenance = "ai_proposed",
            answer_source = (object?)null,
            accepted_variants = Array.Empty<string>(),
            suggested_points_milli = 1_000,
            allow_non_kanji_suggestion = false,
            requires_complete_answer_suggestion = requiresCompleteAnswer,
            answer_order_insensitive_suggestion = answerOrderInsensitive,
            requires_teacher_answer = false,
            confidence = 0.99,
            warnings = Array.Empty<string>(),
        };

    private static object CreateLegacyV4Question() => new
    {
        source_key = "legacy-v4-slot-1",
        display_label = "1",
        question_text = "答えなさい。",
        answer_slot_ordinal = 1,
        answer_slot_count = 1,
        filled_answer_removed = true,
        is_embedded_fill_blank = false,
        question_type = "exact_short_text",
        expected_answer = "東京",
        answer_provenance = "ai_proposed",
        answer_source = (object?)null,
        accepted_variants = Array.Empty<string>(),
        suggested_points_milli = 1_000,
        allow_non_kanji_suggestion = false,
        requires_teacher_answer = false,
        confidence = 0.99,
        warnings = Array.Empty<string>(),
    };

    private static bool IsMultiPlaceholderBlocker(
        TemplateExtractionReviewIssue issue) =>
        issue.Code is
            "question.additional_placeholders_redacted"
            or "question.fill_blank_placeholder_invalid";

    private static int CountPlaceholders(string value) =>
        value.Split("［　］", StringSplitOptions.None).Length - 1;
}
