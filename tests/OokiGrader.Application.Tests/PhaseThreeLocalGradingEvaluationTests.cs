using System.Text.Json;
using OokiGrader.Application.Grading;
using OokiGrader.Domain.Scoring;
using OokiGrader.Domain.Templates;
using DomainQuestionDefinition = OokiGrader.Domain.Templates.QuestionDefinition;

namespace OokiGrader.Application.Tests;

public sealed class PhaseThreeLocalGradingEvaluationTests
{
    [Fact]
    public void EvaluatesVersionedLocalTeacherTruthMatrix()
    {
        var multipleAnswers = ExactQuestion(
            "q-multiple",
            allowNonKanji: true,
            ("answer-multiple-1", "東京", AcceptedAnswerVariantType.Canonical),
            ("answer-multiple-2", "江戸", AcceptedAnswerVariantType.Equivalent));
        var kanjiRequired = ExactQuestion(
            "q-kanji",
            allowNonKanji: false,
            ("answer-kanji-1", "漢字", AcceptedAnswerVariantType.Canonical));
        var rubric = new DomainQuestionDefinition(
            "q-partial",
            "logical-q-partial",
            2,
            "問3",
            "理由を書きなさい。",
            QuestionType.Subjective,
            GradingMode.AiRubric,
            new MilliPoints(1_000),
            new MilliPoints(500),
            allowNonKanji: true,
            requiresReviewAlways: false,
            teacherVerified: true);
        var cases = new[]
        {
            Case("multiple-canonical", "multiple_answers", multipleAnswers, "東京", false, "correct", 1_000),
            Case("multiple-equivalent", "multiple_answers", multipleAnswers, "江戸", false, "correct", 1_000),
            Case("kanji-exact", "kanji_required", kanjiRequired, "漢字", false, "correct", 1_000),
            Case("kanji-kana", "kanji_required", kanjiRequired, "かんじ", false, "incorrect", 0),
            Case("partial-rubric", "partial_credit", rubric, "要素Aのみ", false, "partial", 500),
            Case("incorrect-answer", "incorrect", multipleAnswers, "大阪", false, "incorrect", 0),
            Case("located-blank", "blank", multipleAnswers, string.Empty, true, "blank", 0),
        };
        var samples = new List<GradingAccuracySample>();

        foreach (var item in cases)
        {
            using var response = Response(item);
            var validated = AiGradingResponseValidator.Validate(
                response.RootElement,
                item.CaseId,
                new Dictionary<string, DomainQuestionDefinition>
                {
                    [item.Question.Id] = item.Question,
                });
            var actual = Assert.Single(validated.Observations);
            Assert.Equal(item.ExpectedOutcome, actual.ProposedOutcome);
            Assert.Equal(item.ExpectedPointsMilli, actual.ProposedPointsMilli);
            Assert.Equal(
                item.ExpectedOutcome != "correct",
                actual.ProviderReviewRecommended);
            samples.Add(new GradingAccuracySample(
                item.CaseId,
                item.Category,
                item.ExpectedOutcome,
                item.ExpectedPointsMilli,
                actual.ProposedOutcome,
                actual.ProposedPointsMilli,
                actual.ProviderReviewRecommended));
        }

        var report = GradingAccuracyEvaluator.Evaluate(samples);

        Assert.Equal(7, report.Overall.TotalCases);
        Assert.Equal(10_000, report.Overall.ExactAgreementBasisPoints);
        Assert.Equal(3, report.Overall.SafeAutomaticDecisionCount);
        Assert.Equal(10_000, report.Overall.AutomaticPrecisionBasisPoints);
        Assert.Equal(0, report.Overall.UnsafeAutomaticDecisionCount);
        Assert.Equal(0, report.Overall.IncorrectCreditFalsePositiveCount);
        Assert.Equal(4, report.Overall.RiskCaseCount);
        Assert.Equal(10_000, report.Overall.RiskReviewCoverageBasisPoints);
        AssertEvidenceMatches(report);
    }

    private static DomainQuestionDefinition ExactQuestion(
        string id,
        bool allowNonKanji,
        params (string Id, string Text, AcceptedAnswerVariantType Type)[] answers) =>
        new(
            id,
            $"logical-{id}",
            0,
            "問1",
            "答えを書きなさい。",
            QuestionType.ExactShortText,
            GradingMode.TranscribeThenRules,
            new MilliPoints(1_000),
            new MilliPoints(500),
            allowNonKanji,
            requiresReviewAlways: false,
            teacherVerified: true,
            acceptedAnswers: answers.Select(answer => new AcceptedAnswer(
                answer.Id,
                answer.Text,
                answer.Type,
                AnswerProvenance.TeacherEntered,
                teacherVerified: true)));

    private static EvaluationCase Case(
        string caseId,
        string category,
        DomainQuestionDefinition question,
        string transcription,
        bool blank,
        string expectedOutcome,
        long expectedPointsMilli) =>
        new(
            caseId,
            category,
            question,
            transcription,
            blank,
            expectedOutcome,
            expectedPointsMilli);

    private static JsonDocument Response(EvaluationCase item) =>
        JsonDocument.Parse(
            JsonSerializer.Serialize(new
            {
                schema_version = "answer_transcribe_grade_v1",
                request_key = item.CaseId,
                results = new[]
                {
                    new
                    {
                        question_id = item.Question.Id,
                        transcription = item.Transcription,
                        legibility = "clear",
                        blank = item.Blank,
                        proposed_outcome = item.ExpectedOutcome,
                        proposed_points_milli = item.ExpectedPointsMilli,
                        confidence = 0.99,
                    },
                },
                missing_question_ids = Array.Empty<string>(),
                unexpected_content = false,
            }));

    private static void AssertEvidenceMatches(GradingAccuracyReport report)
    {
        var evidencePath = Path.Combine(
            FindRepositoryRoot(),
            "output",
            "accuracy",
            "phase-3-local-evidence-2026-09-15.json");
        using var evidence = JsonDocument.Parse(File.ReadAllText(evidencePath));
        var grading = evidence.RootElement.GetProperty("grading");

        Assert.False(evidence.RootElement
            .GetProperty("deploymentEvidence")
            .GetBoolean());
        Assert.Equal(
            report.Overall.TotalCases,
            grading.GetProperty("totalCases").GetInt32());
        Assert.Equal(
            report.Overall.ExactAgreementBasisPoints,
            grading.GetProperty("exactAgreementBasisPoints").GetInt32());
        Assert.Equal(
            report.Overall.AutomaticDecisionCount,
            grading.GetProperty("automaticDecisionCount").GetInt32());
        Assert.Equal(
            report.Overall.SafeAutomaticDecisionCount,
            grading.GetProperty("safeAutomaticDecisionCount").GetInt32());
        Assert.Equal(
            report.Overall.UnsafeAutomaticDecisionCount,
            grading.GetProperty("unsafeAutomaticDecisionCount").GetInt32());
        Assert.Equal(
            report.Overall.IncorrectCreditFalsePositiveCount,
            grading.GetProperty("incorrectCreditFalsePositiveCount").GetInt32());
        Assert.Equal(
            report.Overall.RiskCaseReviewCount,
            grading.GetProperty("riskCaseReviewCount").GetInt32());
        Assert.Equal(
            report.Overall.RiskReviewCoverageBasisPoints,
            grading.GetProperty("riskReviewCoverageBasisPoints").GetInt32());
        Assert.Equal(
            report.Overall.TotalCases,
            grading.GetProperty("cases").GetArrayLength());
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "global.json"))
                && Directory.Exists(
                    Path.Combine(current.FullName, "installer")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            "The repository root could not be located.");
    }

    private sealed record EvaluationCase(
        string CaseId,
        string Category,
        DomainQuestionDefinition Question,
        string Transcription,
        bool Blank,
        string ExpectedOutcome,
        long ExpectedPointsMilli);
}
